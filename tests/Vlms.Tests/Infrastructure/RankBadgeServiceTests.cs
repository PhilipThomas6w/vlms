using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Badges;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="RankBadgeService"/> (functional.md FR-014): only an Admin uploads or lists badge
/// images; the image is type- and size-checked; the first upload creates the rank's
/// <see cref="RankBadge"/> row and a later upload replaces its image without a second row.
///
/// Same SQLite-in-memory-via-DI pattern as <see cref="StudentAdministrationServiceTests"/>.
/// </summary>
public sealed class RankBadgeServiceTests : IDisposable
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] WebpBytes = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");
    private static readonly byte[] SvgBytes = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");

    private readonly SqliteConnection _anchorConnection;
    private readonly string _connectionString;
    private readonly InMemoryBlobStorage _blobs = new();

    public RankBadgeServiceTests()
    {
        _connectionString = $"Data Source=file:vlms-rankbadge-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchorConnection = new SqliteConnection(_connectionString);
        _anchorConnection.Open();

        using var schema = CreateCaller(userId: 1, Role.Admin);
        schema.Context.Database.EnsureCreated();

        schema.Context.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        schema.Context.Ranks.Add(new Rank { Id = 2, Order = 2, Code = "R2", Name = "Novice" });
        schema.Context.SaveChanges();
    }

    public void Dispose() => _anchorConnection.Dispose();

    private sealed class Caller(ServiceProvider provider, IServiceScope scope, VlmsDbContext context, RankBadgeService service) : IDisposable
    {
        public VlmsDbContext Context { get; } = context;

        public RankBadgeService Service { get; } = service;

        public void Dispose()
        {
            Context.Dispose();
            scope.Dispose();
            provider.Dispose();
        }
    }

    private Caller CreateCaller(int? userId, params Role[] roles)
    {
        var currentUser = new FakeCurrentUserContext(userId, roles);

        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(currentUser);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VlmsDbContext>();

        return new Caller(provider, scope, context, new RankBadgeService(context, _blobs, currentUser));
    }

    private static byte[] Padded(byte[] prefix, int length)
    {
        var bytes = new byte[length];
        prefix.CopyTo(bytes, 0);
        return bytes;
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Approver)]
    [InlineData(Role.Student)]
    [InlineData(Role.SafeguardingOfficer)]
    public async Task NonAdmin_IsRefused_AndNothingIsWritten(Role role)
    {
        using var caller = CreateCaller(userId: 10, role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.Service.ListRanksAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            caller.Service.SetImageAsync(1, "badge.png", PngSignature));

        Assert.Empty(caller.Context.RankBadges);
        Assert.False(_blobs.TryGetBlob("rank-badges/1.png", out _));
    }

    [Theory]
    [InlineData(Role.Teacher)]
    [InlineData(Role.Parent)]
    [InlineData(Role.Student)]
    [InlineData(Role.Approver)]
    public async Task LibraryRoles_CanViewAnImage_ButNotUpload(Role role)
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);
        await admin.Service.SetImageAsync(1, "badge.png", PngSignature);

        using var viewer = CreateCaller(userId: 10, role);
        var image = await viewer.Service.OpenImageAsync(1);

        Assert.NotNull(image);
        Assert.Equal("image/png", image.ContentType);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            viewer.Service.SetImageAsync(1, "badge.png", PngSignature));
    }

    [Fact]
    public async Task SafeguardingOfficer_CannotViewAnImage()
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);
        await admin.Service.SetImageAsync(1, "badge.png", PngSignature);

        using var officer = CreateCaller(userId: 11, Role.SafeguardingOfficer);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => officer.Service.OpenImageAsync(1));
    }

    [Theory]
    [InlineData("badge.png", 0)]
    [InlineData("badge.jpg", 1)]
    [InlineData("badge.jpeg", 1)]
    [InlineData("badge.webp", 2)]
    [InlineData("badge.svg", 3)]
    public async Task ValidImages_AreAccepted(string fileName, int kind)
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);
        var content = kind switch
        {
            0 => PngSignature,
            1 => JpegBytes,
            2 => WebpBytes,
            _ => SvgBytes
        };

        await admin.Service.SetImageAsync(1, fileName, content);

        var row = await admin.Context.RankBadges.AsNoTracking().SingleAsync();
        Assert.Equal(1, row.RankId);
    }

    [Theory]
    [InlineData("badge.gif")]
    [InlineData("badge.pdf")]
    [InlineData("badge")]
    [InlineData("badge.svg.exe")]
    public async Task UnsupportedExtension_IsRefused(string fileName)
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);

        await Assert.ThrowsAsync<ArgumentException>(() => admin.Service.SetImageAsync(1, fileName, PngSignature));
        Assert.Empty(admin.Context.RankBadges);
    }

    [Theory]
    [InlineData("badge.png", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData("badge.jpg", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("badge.webp", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData("badge.svg", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    public async Task ContentThatDoesNotMatchExtension_IsRefused(string fileName, byte[] content)
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);

        await Assert.ThrowsAsync<ArgumentException>(() => admin.Service.SetImageAsync(1, fileName, content));
        Assert.Empty(admin.Context.RankBadges);
        Assert.False(_blobs.TryGetBlob($"rank-badges/1{Path.GetExtension(fileName)}", out _));
    }

    [Fact]
    public async Task SvgWithoutAnSvgElement_IsRefused()
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            admin.Service.SetImageAsync(1, "badge.svg", Encoding.UTF8.GetBytes("<html><body>not svg</body></html>")));
    }

    [Fact]
    public async Task EmptyFile_IsRefused()
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);

        await Assert.ThrowsAsync<ArgumentException>(() => admin.Service.SetImageAsync(1, "badge.png", []));
    }

    [Fact]
    public async Task FileOfExactlyTwoMegabytes_IsAccepted_AndOneByteMore_IsRefused()
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);

        await admin.Service.SetImageAsync(1, "badge.png", Padded(PngSignature, (int)RankBadgeService.MaxBytes));

        var oversized = Padded(PngSignature, (int)RankBadgeService.MaxBytes + 1);
        await Assert.ThrowsAsync<ArgumentException>(() => admin.Service.SetImageAsync(2, "badge.png", oversized));
        Assert.Single(admin.Context.RankBadges);
    }

    [Fact]
    public async Task UnknownRank_IsRefused()
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);

        await Assert.ThrowsAsync<ArgumentException>(() => admin.Service.SetImageAsync(999, "badge.png", PngSignature));
    }

    [Fact]
    public async Task FirstUpload_CreatesOneRow_AndLaterUpload_ReplacesTheImageInPlace()
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);

        await admin.Service.SetImageAsync(1, "first.png", PngSignature);

        var created = await admin.Context.RankBadges.AsNoTracking().SingleAsync();
        Assert.Equal(1, created.RankId);
        Assert.Equal("rank-badges/1.png", created.ImageBlobKey);
        Assert.True(_blobs.TryGetBlob("rank-badges/1.png", out var firstBytes));
        Assert.Equal(PngSignature, firstBytes);

        var before = await admin.Service.ListRanksAsync();
        Assert.True(before.Single(r => r.RankId == 1).HasImage);
        Assert.False(before.Single(r => r.RankId == 2).HasImage);

        await admin.Service.SetImageAsync(1, "second.svg", SvgBytes);

        var replaced = await admin.Context.RankBadges.AsNoTracking().ToListAsync();
        var row = Assert.Single(replaced);
        Assert.Equal(created.Id, row.Id);
        Assert.Equal("rank-badges/1.svg", row.ImageBlobKey);
        Assert.True(_blobs.TryGetBlob("rank-badges/1.svg", out var svgBytes));
        Assert.Equal(SvgBytes, svgBytes);

        var image = await admin.Service.OpenImageAsync(1);
        Assert.NotNull(image);
        Assert.Equal("image/svg+xml", image.ContentType);
        Assert.Equal(SvgBytes, image.Content);
    }

    [Fact]
    public async Task SecondRankBadgeForSameRank_IsRejectedByTheDatabase()
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);
        await admin.Service.SetImageAsync(1, "first.png", PngSignature);

        admin.Context.RankBadges.Add(new RankBadge { Id = 99, RankId = 1, ImageBlobKey = "rank-badges/1-dup.png" });

        await Assert.ThrowsAsync<DbUpdateException>(() => admin.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task OpenImage_ForRankWithoutBadge_ReturnsNull()
    {
        using var admin = CreateCaller(userId: 1, Role.Admin);

        Assert.Null(await admin.Service.OpenImageAsync(2));
    }
}
