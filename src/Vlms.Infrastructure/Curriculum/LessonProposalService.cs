using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;

namespace Vlms.Infrastructure.Curriculum;

/// <summary>
/// The curriculum-management workflow (docs/design/low-level-design.md "LessonProposalService",
/// docs/requirements/functional.md "Curriculum management"): any Teacher may propose a change to a
/// <see cref="Lesson"/> (create/edit/retire); only the Approver role may approve or reject the
/// proposal; a rejected proposal may be resubmitted (by the same or a different Teacher), chained
/// back to the original via <see cref="LessonChangeProposal.ResubmissionOfProposalId"/>.
///
/// Authorization: this service does its own role checks (<see cref="RequireRole"/>) rather than
/// relying solely on the caller having already passed the ASP.NET Core <c>RequireTeacher</c>/
/// <c>RequireApprover</c> policy at the page level. Deliberate defense-in-depth, not duplication
/// for its own sake — the same reasoning as VlmsDbContext's sensitive-data query filters
/// (adr/0004-sensitive-data-access-control.md): a caller reaching this service by any path other
/// than the intended UI (a future API endpoint, a bug in page-level gating) must still be denied
/// here, not just kept out by the router. Same layering as
/// <see cref="Vlms.Infrastructure.Guardianship.GuardianLinkService"/>: takes
/// <see cref="VlmsDbContext"/> directly, does not itself touch ASP.NET Core authorization types.
///
/// The Approver role is curriculum-only (Vlms.Domain.Role's doc comment; VISION.md): nothing in
/// this service reads or writes <c>DbsCheck</c>/<c>ConsentSensitiveDetails</c>, and
/// <see cref="Lesson"/>/<see cref="LessonChangeProposal"/> carry no sensitive-data query filter —
/// there is nothing here to bypass.
///
/// <see cref="Lesson"/> and <see cref="LessonChangeProposal"/> use init-only properties throughout
/// (this codebase's domain-entity convention — see openwiki/domain.md). EF Core updates them via
/// <c>ChangeTracking.CurrentValues.SetValues</c>/<c>EntityEntry&lt;T&gt;</c>, which write through
/// the compiler-generated backing fields rather than the C# "init" accessor — the officially
/// documented pattern for updating entities that don't expose public setters
/// (https://learn.microsoft.com/ef/core/change-tracking/identity-resolution#updating-an-entity),
/// not a workaround.
/// </summary>
public sealed class LessonProposalService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly IBlobStorage? _blobs;

    /// <param name="blobs">Needed only by <see cref="OpenProposalFileAsync"/>.</param>
    public LessonProposalService(VlmsDbContext db, ICurrentUserContext currentUser, IBlobStorage? blobs = null)
    {
        _db = db;
        _currentUser = currentUser;
        _blobs = blobs;
    }

    /// <summary>Any Teacher may propose a create/edit/retire. Creates a Pending proposal.</summary>
    public async Task<LessonChangeProposal> ProposeAsync(
        int? lessonId, LessonChangeType changeType, ProposedLessonContent content, CancellationToken ct = default)
    {
        RequireRole(Role.Teacher);
        var proposerId = RequireResolvedUserId();

        await ValidateLessonReferenceAsync(lessonId, changeType, content, [proposerId], ct);

        var proposal = new LessonChangeProposal
        {
            Id = await NextId(_db.LessonChangeProposals, ct),
            LessonId = lessonId,
            ProposedByUserId = proposerId,
            ChangeType = changeType,
            ProposedContent = JsonSerializer.Serialize(content),
            Status = ProposalStatus.Pending,
            SubmittedAt = DateTime.UtcNow
        };

        _db.LessonChangeProposals.Add(proposal);
        await _db.SaveChangesAsync(ct);
        return proposal;
    }

    /// <summary>
    /// Only the Approver role. Applies <see cref="LessonChangeProposal.ProposedContent"/> onto the
    /// <see cref="Lesson"/> — creating it if <see cref="LessonChangeProposal.LessonId"/> is null,
    /// updating (or retiring, i.e. setting IsActive false) it otherwise — then marks the proposal
    /// Approved.
    /// </summary>
    public async Task<LessonChangeProposal> ApproveAsync(int proposalId, CancellationToken ct = default)
    {
        RequireRole(Role.Approver);
        var approverId = RequireResolvedUserId();

        var proposal = await _db.LessonChangeProposals.SingleAsync(p => p.Id == proposalId, ct);
        RequirePending(proposal);

        var content = JsonSerializer.Deserialize<ProposedLessonContent>(proposal.ProposedContent)
            ?? throw new InvalidOperationException($"Proposal {proposalId}'s ProposedContent could not be parsed.");

        var lessonId = await ApplyToLessonAsync(proposal, content, ct);

        _db.Entry(proposal).CurrentValues.SetValues(new LessonChangeProposal
        {
            Id = proposal.Id,
            LessonId = lessonId,
            ProposedByUserId = proposal.ProposedByUserId,
            ChangeType = proposal.ChangeType,
            ProposedContent = proposal.ProposedContent,
            Status = ProposalStatus.Approved,
            ApproverUserId = approverId,
            ApprovalComments = null,
            SubmittedAt = proposal.SubmittedAt,
            DecidedAt = DateTime.UtcNow,
            ResubmissionOfProposalId = proposal.ResubmissionOfProposalId
        });

        await _db.SaveChangesAsync(ct);
        return proposal;
    }

    /// <summary>Only the Approver role. Rejects with mandatory comments; the Lesson is untouched.</summary>
    public async Task<LessonChangeProposal> RejectAsync(int proposalId, string comments, CancellationToken ct = default)
    {
        RequireRole(Role.Approver);
        var approverId = RequireResolvedUserId();
        ArgumentException.ThrowIfNullOrWhiteSpace(comments);

        var proposal = await _db.LessonChangeProposals.SingleAsync(p => p.Id == proposalId, ct);
        RequirePending(proposal);

        _db.Entry(proposal).CurrentValues.SetValues(new LessonChangeProposal
        {
            Id = proposal.Id,
            LessonId = proposal.LessonId,
            ProposedByUserId = proposal.ProposedByUserId,
            ChangeType = proposal.ChangeType,
            ProposedContent = proposal.ProposedContent,
            Status = ProposalStatus.Rejected,
            ApproverUserId = approverId,
            ApprovalComments = comments,
            SubmittedAt = proposal.SubmittedAt,
            DecidedAt = DateTime.UtcNow,
            ResubmissionOfProposalId = proposal.ResubmissionOfProposalId
        });

        await _db.SaveChangesAsync(ct);
        return proposal;
    }

    /// <summary>
    /// Any Teacher (the original proposer or a different one) may resubmit a rejected proposal —
    /// creates a new Pending proposal chained back to the original via
    /// <see cref="LessonChangeProposal.ResubmissionOfProposalId"/>. The original row is untouched
    /// (stays Rejected, keeps its <see cref="LessonChangeProposal.ApprovalComments"/>).
    /// </summary>
    public async Task<LessonChangeProposal> ResubmitAsync(
        int originalProposalId, ProposedLessonContent content, CancellationToken ct = default)
    {
        RequireRole(Role.Teacher);
        var proposerId = RequireResolvedUserId();

        var original = await _db.LessonChangeProposals.SingleAsync(p => p.Id == originalProposalId, ct);
        if (original.Status != ProposalStatus.Rejected)
        {
            throw new InvalidOperationException(
                $"Only a rejected proposal can be resubmitted (proposal {originalProposalId} has Status={original.Status}).");
        }

        await ValidateLessonReferenceAsync(
            original.LessonId, original.ChangeType, content, [proposerId, original.ProposedByUserId], ct);

        var resubmission = new LessonChangeProposal
        {
            Id = await NextId(_db.LessonChangeProposals, ct),
            LessonId = original.LessonId,
            ProposedByUserId = proposerId,
            ChangeType = original.ChangeType,
            ProposedContent = JsonSerializer.Serialize(content),
            Status = ProposalStatus.Pending,
            SubmittedAt = DateTime.UtcNow,
            ResubmissionOfProposalId = original.Id
        };

        _db.LessonChangeProposals.Add(resubmission);
        await _db.SaveChangesAsync(ct);
        return resubmission;
    }

    private async Task<int> ApplyToLessonAsync(LessonChangeProposal proposal, ProposedLessonContent content, CancellationToken ct)
    {
        if (proposal.LessonId is null)
        {
            var newLesson = new Lesson
            {
                Id = await NextId(_db.Lessons, ct),
                RankId = content.RankId
                    ?? throw new InvalidOperationException("RankId is required to create a new Lesson."),
                Code = content.Code,
                Title = content.Title,
                Order = content.Order,
                IsActive = content.IsActive
            };
            _db.Lessons.Add(newLesson);
            await ReplaceTagsAndFilesAsync(newLesson.Id, content, ct);
            return newLesson.Id;
        }

        var lesson = await _db.Lessons.SingleAsync(l => l.Id == proposal.LessonId, ct);
        _db.Entry(lesson).CurrentValues.SetValues(new Lesson
        {
            Id = lesson.Id,
            RankId = content.RankId ?? lesson.RankId,
            Code = content.Code,
            Title = content.Title,
            Order = content.Order,
            IsActive = content.IsActive,
            // Proposals do not carry the lesson text yet (adr/0012 decision 8); keep it and its pictures.
            Body = lesson.Body,
            ContentVersion = lesson.ContentVersion
        });
        await ReplaceTagsAndFilesAsync(lesson.Id, content, ct);
        return lesson.Id;
    }

    // Tags and files are replaced wholesale (see ProposedLessonContent). Only the database
    // rows change: the blobs themselves are left in storage, as certificate PDFs are.
    private async Task ReplaceTagsAndFilesAsync(int lessonId, ProposedLessonContent content, CancellationToken ct)
    {
        _db.LessonTagAssignments.RemoveRange(
            await _db.LessonTagAssignments.Where(t => t.LessonId == lessonId).ToListAsync(ct));
        _db.LessonFiles.RemoveRange(
            await _db.LessonFiles.Where(f => f.LessonId == lessonId).ToListAsync(ct));

        foreach (var tag in content.Tags.Distinct())
        {
            _db.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = lessonId, Tag = tag });
        }

        var nextFileId = (await _db.LessonFiles.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
        for (var i = 0; i < content.Files.Count; i++)
        {
            _db.LessonFiles.Add(new LessonFile
            {
                Id = nextFileId + i,
                LessonId = lessonId,
                Order = i + 1,
                FileName = content.Files[i].FileName,
                BlobKey = content.Files[i].BlobKey
            });
        }
    }

    private async Task ValidateLessonReferenceAsync(
        int? lessonId, LessonChangeType changeType, ProposedLessonContent content, int[] pendingOwners, CancellationToken ct)
    {
        if (changeType == LessonChangeType.Create && lessonId is not null)
        {
            throw new InvalidOperationException("A Create proposal must not reference an existing LessonId.");
        }

        if (changeType != LessonChangeType.Create && lessonId is null)
        {
            throw new InvalidOperationException("Edit/Retire proposals must reference an existing LessonId.");
        }

        if (changeType == LessonChangeType.Retire && content.IsActive)
        {
            throw new InvalidOperationException("A Retire proposal's content must set IsActive to false.");
        }

        if (content.Order < 1)
        {
            throw new InvalidOperationException("A lesson's Order must be 1 or more.");
        }

        if (content.Tags.Count == 0)
        {
            throw new InvalidOperationException("A lesson must have at least one tag.");
        }

        if (content.Files.Any(f => string.IsNullOrWhiteSpace(f.FileName) || string.IsNullOrWhiteSpace(f.BlobKey)))
        {
            throw new InvalidOperationException("Every lesson file needs a file name and a content location.");
        }

        // A file may only point at a blob the proposer is entitled to attach: one of the lesson's
        // current files, or a pending upload under the proposer's own prefix (or, on a resubmission,
        // the original proposer's). Anything else (another lesson's file, a certificate, another
        // teacher's pending upload) is refused, so a proposal cannot be used to expose arbitrary blobs.
        var currentKeys = lessonId is null
            ? new HashSet<string>()
            : (await _db.LessonFiles.Where(f => f.LessonId == lessonId).Select(f => f.BlobKey).ToListAsync(ct)).ToHashSet();
        foreach (var file in content.Files)
        {
            var key = file.BlobKey;
            var isPending = pendingOwners.Any(owner => PendingKeys.BelongsTo(key, owner));
            if (!isPending && !currentKeys.Contains(key))
            {
                throw new InvalidOperationException(
                    $"File '{file.FileName}' must be uploaded, or be one of the lesson's current files.");
            }
        }
    }

    /// <summary>
    /// Opens the index-th (zero-based) file of a proposal, whatever the proposal's status. Only the
    /// proposer (a Teacher) and Approvers may open it; everyone else is refused. Pending blobs are
    /// reachable only through here, never through the lesson library.
    /// </summary>
    public async Task<LessonFileContent?> OpenProposalFileAsync(int proposalId, int index, CancellationToken ct = default)
    {
        var isApprover = _currentUser.HasRole(Role.Approver);
        if (!isApprover && !_currentUser.HasRole(Role.Teacher))
        {
            throw new UnauthorizedAccessException("Caller may not open proposal files.");
        }

        var userId = RequireResolvedUserId();
        var proposal = await _db.LessonChangeProposals.AsNoTracking().SingleOrDefaultAsync(p => p.Id == proposalId, ct);
        if (proposal is null)
        {
            return null;
        }

        if (!isApprover && proposal.ProposedByUserId != userId)
        {
            throw new UnauthorizedAccessException("Only the proposer or an Approver may open this proposal's files.");
        }

        var content = JsonSerializer.Deserialize<ProposedLessonContent>(proposal.ProposedContent);
        if (content is null || index < 0 || index >= content.Files.Count)
        {
            return null;
        }

        var file = content.Files[index];
        var bytes = await (_blobs ?? throw new InvalidOperationException("No blob storage is configured."))
            .ReadAsync(file.BlobKey, ct);
        return bytes is null ? null : new LessonFileContent(file.FileName, bytes);
    }

    /// <summary>
    /// Approver only. The proposal laid out against the current lesson (for an edit or retirement),
    /// with each difference flagged.
    /// </summary>
    public async Task<ProposalReview> GetReviewAsync(int proposalId, CancellationToken ct = default)
    {
        RequireRole(Role.Approver);

        var proposal = await _db.LessonChangeProposals.AsNoTracking()
            .Include(p => p.ProposedByUser)
            .SingleAsync(p => p.Id == proposalId, ct);
        var proposed = JsonSerializer.Deserialize<ProposedLessonContent>(proposal.ProposedContent)
            ?? throw new InvalidOperationException($"Proposal {proposalId}'s ProposedContent could not be parsed.");

        Lesson? current = proposal.LessonId is null
            ? null
            : await _db.Lessons.AsNoTracking().Include(l => l.Rank).Include(l => l.Tags).Include(l => l.Files)
                .SingleAsync(l => l.Id == proposal.LessonId, ct);

        var rankName = current?.Rank?.Name
            ?? (proposed.RankId is int rid ? (await _db.Ranks.AsNoTracking().SingleOrDefaultAsync(r => r.Id == rid, ct))?.Name : null)
            ?? "";

        return ProposalReview.Build(proposal, proposed, current, rankName);
    }

    private static void RequirePending(LessonChangeProposal proposal)
    {
        if (proposal.Status != ProposalStatus.Pending)
        {
            throw new InvalidOperationException($"Proposal {proposal.Id} is not pending (Status={proposal.Status}).");
        }
    }

    private void RequireRole(Role role)
    {
        if (!_currentUser.HasRole(role))
        {
            throw new UnauthorizedAccessException($"Caller does not hold the {role} role.");
        }
    }

    private int RequireResolvedUserId() =>
        _currentUser.UserId ?? throw new UnauthorizedAccessException("Caller has no resolved UserId.");

    // Ids are application-assigned (ValueGeneratedNever — VlmsDbContext.OnModelCreating), same
    // pattern as UserProvisioningService: computing max+1 is race-prone under concurrent writers,
    // acceptable at this system's tens-of-users scale (VISION.md).
    private async Task<int> NextId(DbSet<Lesson> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;

    private async Task<int> NextId(DbSet<LessonChangeProposal> set, CancellationToken ct) =>
        (await set.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1;
}
