using System.Text;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Curriculum;

namespace Vlms.Infrastructure.Badges;

/// <summary>
/// Rank badge images (functional.md FR-014, user-journeys.md J6 step 5): an Admin uploads one badge
/// image per rank; the image is shown on the parent dashboard next to each badge a child holds.
///
/// Writes (<see cref="SetImageAsync"/>) are Admin-only. Reads of image bytes
/// (<see cref="OpenImageAsync"/>) use the same roles as the lesson library
/// (<see cref="LessonLibraryService.LibraryRoles"/>), because the image endpoint is behind the
/// <c>RequireLessonLibrary</c> policy. Both check the role inside the service too (defence in depth,
/// as in <see cref="LessonLibraryService"/>).
///
/// Storage: one <see cref="RankBadge"/> row per rank (created on first upload, updated on replace)
/// and the bytes at <c>rank-badges/{rankId}{extension}</c>. The blob key never comes from the caller.
/// <see cref="IBlobStorage"/> has no delete, so replacing an image with a different extension leaves
/// the old blob on disk, unreferenced. The row is updated to the new key.
/// </summary>
public sealed class RankBadgeService
{
    /// <summary>Largest accepted badge image: 2 MB (FR-014 upload limit).</summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    private const string SvgContentType = "image/svg+xml";
    private const string FallbackContentType = "application/octet-stream";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".svg"] = SvgContentType
    };

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly VlmsDbContext _db;
    private readonly IBlobStorage _blobStorage;
    private readonly ICurrentUserContext _currentUser;

    public RankBadgeService(VlmsDbContext db, IBlobStorage blobStorage, ICurrentUserContext currentUser)
    {
        _db = db;
        _blobStorage = blobStorage;
        _currentUser = currentUser;
    }

    /// <summary>One rank with whether it currently has a badge image. Admin-only.</summary>
    public sealed record RankBadgeRow(int RankId, string RankName, bool HasImage);

    /// <summary>The bytes and content type of a rank's badge image.</summary>
    public sealed record BadgeImage(string ContentType, byte[] Content);

    /// <summary>Every rank in delivery order, with whether it has a badge image. Admin-only.</summary>
    public async Task<IReadOnlyList<RankBadgeRow>> ListRanksAsync(CancellationToken ct = default)
    {
        RequireAdmin();

        var withImage = await RankIdsWithImageAsync(_db, _blobStorage, ct);
        var ranks = await _db.Ranks.AsNoTracking()
            .OrderBy(r => r.Order)
            .Select(r => new { r.Id, r.Name })
            .ToListAsync(ct);
        return ranks.Select(r => new RankBadgeRow(r.Id, r.Name, withImage.Contains(r.Id))).ToList();
    }

    /// <summary>
    /// The ids of the ranks that have a badge image: the rank's <see cref="RankBadge"/> row exists AND
    /// its blob is in storage. A row whose blob is missing does not count, so the medallion shows the
    /// rank's number rather than a broken image. Checks that each candidate blob exists (without
    /// reading it), so callers should compute this once per page load, not once per image request.
    /// </summary>
    public static async Task<HashSet<int>> RankIdsWithImageAsync(VlmsDbContext db, IBlobStorage blobStorage, CancellationToken ct = default)
    {
        var rows = await db.RankBadges.AsNoTracking()
            .Select(b => new { b.RankId, b.ImageBlobKey })
            .ToListAsync(ct);

        var rankIds = new HashSet<int>();
        foreach (var row in rows)
        {
            if (await blobStorage.ExistsAsync(row.ImageBlobKey, ct))
            {
                rankIds.Add(row.RankId);
            }
        }

        return rankIds;
    }

    /// <summary>
    /// Sets the badge image for a rank, creating its <see cref="RankBadge"/> row on the first upload and
    /// replacing the image on later ones. Admin-only. Throws <see cref="ArgumentException"/> (with a
    /// message safe to show to the Admin) if the file type, content, size or rank is not valid.
    /// </summary>
    public async Task SetImageAsync(int rankId, string fileName, byte[] content, CancellationToken ct = default)
    {
        RequireAdmin();

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!ContentTypes.ContainsKey(extension))
        {
            throw new ArgumentException("Badge images must be a PNG, JPEG, WebP or SVG file.");
        }

        if (content.Length == 0 || content.Length > MaxBytes)
        {
            throw new ArgumentException("Badge images must be no larger than 2 MB.");
        }

        if (!MatchesExtension(extension, content))
        {
            throw new ArgumentException($"The file is not a valid {extension} image.");
        }

        if (!await _db.Ranks.AnyAsync(r => r.Id == rankId, ct))
        {
            throw new ArgumentException("The rank does not exist.");
        }

        var blobKey = $"rank-badges/{rankId}{extension}";
        await _blobStorage.UploadAsync(blobKey, content, ct);

        var badge = await _db.RankBadges.SingleOrDefaultAsync(b => b.RankId == rankId, ct);
        if (badge is null)
        {
            _db.RankBadges.Add(new RankBadge
            {
                Id = await NextRankBadgeIdAsync(ct),
                RankId = rankId,
                ImageBlobKey = blobKey
            });
        }
        else
        {
            _db.Entry(badge).CurrentValues.SetValues(new { ImageBlobKey = blobKey });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The badge image for a rank, or null if the rank has no badge or its blob is missing. Throws
    /// <see cref="UnauthorizedAccessException"/> for a caller outside <see cref="LessonLibraryService.LibraryRoles"/>.
    /// </summary>
    public async Task<BadgeImage?> OpenImageAsync(int rankId, CancellationToken ct = default)
    {
        if (!LessonLibraryService.LibraryRoles.Any(_currentUser.HasRole))
        {
            throw new UnauthorizedAccessException("Caller does not hold a role that may view badge images.");
        }

        var blobKey = await _db.RankBadges.AsNoTracking()
            .Where(b => b.RankId == rankId)
            .Select(b => b.ImageBlobKey)
            .SingleOrDefaultAsync(ct);
        if (blobKey is null)
        {
            return null;
        }

        var content = await _blobStorage.ReadAsync(blobKey, ct);
        if (content is null)
        {
            return null;
        }

        var contentType = ContentTypes.TryGetValue(Path.GetExtension(blobKey), out var type) ? type : FallbackContentType;
        return new BadgeImage(contentType, content);
    }

    /// <summary>Checks the bytes against the file's extension, so the stored type matches what the file is.</summary>
    private static bool MatchesExtension(string extension, byte[] content) => extension switch
    {
        ".png" => content.AsSpan().StartsWith(PngSignature),
        ".jpg" or ".jpeg" => content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
        ".webp" => content.Length >= 12
            && Encoding.ASCII.GetString(content, 0, 4) == "RIFF"
            && Encoding.ASCII.GetString(content, 8, 4) == "WEBP",
        ".svg" => LooksLikeSvg(content),
        _ => false
    };

    private static bool LooksLikeSvg(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return text.StartsWith('<') && text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    // Same application-assigned-id pattern as PromotionService.NextId.
    private async Task<int> NextRankBadgeIdAsync(CancellationToken ct) =>
        (await _db.RankBadges.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;

    private void RequireAdmin()
    {
        // Rank badges are national (FR-021, docs/adr/0010-chapters.md): a chapter Admin may not change
        // every chapter's artwork, so only a National Administrator manages them.
        if (!_currentUser.HasRole(Role.NationalAdministrator))
        {
            throw new UnauthorizedAccessException("Caller must be a National Administrator to manage rank badge images.");
        }
    }
}
