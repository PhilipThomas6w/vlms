using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Safeguarding;

namespace Vlms.Infrastructure.Registration;

/// <summary>The parent's own details on a registration application (adr/0008 answer 5: all required).</summary>
public sealed record ApplicantDetails(string Phone, string PostalAddress);

/// <summary>One child on a registration application: the child, the applicant's relationship to them, and the full consent form (FR-001 as split by adr/0008 decision 5).</summary>
public sealed record ChildApplicationForm(
    string ChildName,
    DateOnly DateOfBirth,
    string Relationship,
    bool PhotoMediaConsent,
    bool TransportConsent,
    bool OffsiteTripConsent,
    bool DataSharingConsent,
    string EmergencyContact,
    string? EmergencyMedicalInfo,
    string? DietaryRequirements,
    string? SpecialEducationalNeeds);

/// <summary>The restricted part of an application, read through the audited, query-filtered entity.</summary>
public sealed record ApplicationDetailsView(
    string EmergencyContact,
    string? EmergencyMedicalInfo,
    string? DietaryRequirements,
    string? SpecialEducationalNeeds,
    string? InformationRequest,
    string? ParentResponse,
    string? DecisionComments);

/// <summary>One application for display. <see cref="Details"/> is null when the caller may not read it or it was not asked for.</summary>
public sealed record ApplicationView(
    int Id,
    StudentApplicationStatus Status,
    int ApplicantUserId,
    string ApplicantName,
    string ApplicantEmail,
    string ParentPhone,
    string ParentPostalAddress,
    string Relationship,
    string ChildName,
    DateOnly ChildDateOfBirth,
    bool PhotoMediaConsent,
    bool TransportConsent,
    bool OffsiteTripConsent,
    bool DataSharingConsent,
    DateTime SubmittedAt,
    DateTime LastSubmittedAt,
    string? SafeguardingReviewerName,
    DateTime? SafeguardingReviewedAt,
    string? AdminDeciderName,
    DateTime? AdminDecidedAt,
    int? StudentId,
    bool LinkedToExistingStudent,
    ApplicationDetailsView? Details);

/// <summary>An existing student with the same name and date of birth as the applicant's child (adr/0008 answer 6).</summary>
public sealed record ExistingStudentMatch(
    int StudentId, string Name, DateOnly DateOfBirth, StudentStatus Status, string CurrentRankName, IReadOnlyList<string> GuardianNames);

/// <summary>A rank the Admin can choose as the starting rank.</summary>
public sealed record RankOption(int Id, int Order, string Name);

/// <summary>What the Admin's approval page needs.</summary>
public sealed record ApplicationForApproval(
    ApplicationView Application, IReadOnlyList<ExistingStudentMatch> SameNameAndDateOfBirth, IReadOnlyList<RankOption> Ranks);

/// <summary>
/// Parent registration with two-stage approval (functional.md FR-004 as amended 22/10/2026,
/// adr/0008 answers 1, 4, 5, 6).
///
/// <b>The application.</b> A signed-in adult (any account that is not a student sign-in and has not
/// left; they need hold no role yet) applies to register one or more children, giving their phone
/// number and postal address, their relationship to each child, and a full consent form for each.
/// Each child becomes one <see cref="StudentApplication"/> with its sensitive fields in a
/// <see cref="StudentApplicationSensitiveDetails"/> row (restricted and read-audited). Nothing else is
/// created: no <see cref="Student"/>, <see cref="StudentGuardianLink"/>, <see cref="ParentGuardian"/>,
/// <see cref="ConsentRecord"/> or role.
///
/// <b>Stage 1, Safeguarding Officer only</b> (not Admin, unless the Admin also holds the role), with
/// MFA: approve (to stage 2), ask the parent for more information (the parent edits, answers and
/// resubmits, and it returns to stage 1), or reject with comments.
///
/// <b>Stage 2, Admin only</b>, with MFA: approve, choosing any starting rank or linking this parent
/// to an existing student with the same name and date of birth; or reject with comments. Approval,
/// in one transaction: the <see cref="Student"/> and its open <see cref="StudentRankProgress"/> (or
/// the existing student), the applicant's <see cref="ParentGuardian"/> if they have none (linked to
/// their own account, so no invite is needed), the <see cref="StudentGuardianLink"/>, an approved
/// <see cref="ConsentRecord"/> with its <see cref="ConsentSensitiveDetails"/> copied from the
/// application, and the Parent role (added to any roles already held, with the security stamp
/// rotated as <see cref="RoleAssignmentService"/> does).
///
/// <b>One person, both stages.</b> Allowed: a user holding both roles may approve at stage 1 and
/// then at stage 2. Each stage records who acted. Whether that should be allowed is an open question
/// for Philip (adr/0008).
///
/// <b>Duplicates</b> (adr/0008 answer 6): the same child name under the same applicant
/// (case-insensitive, whitespace collapsed) is refused, whether the earlier one is an application
/// still open or approved, or a student already linked to the applicant. Rejected applications do
/// not count. Children of different parents with the same name are different children; the Admin
/// sees existing students with the same name and date of birth and may link instead.
///
/// <b>Access.</b> Every method checks its own role or applicant ownership (defence in depth, as in
/// every service here). Sensitive reads go through the query-filtered entity only, never
/// <c>IgnoreQueryFilters</c>, so they are audited.
/// </summary>
public sealed class StudentApplicationService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly UserManager<VlmsIdentityUser> _userManager;
    private readonly RegistrationNotificationService? _notifications;

    public StudentApplicationService(
        VlmsDbContext db,
        ICurrentUserContext currentUser,
        UserManager<VlmsIdentityUser> userManager,
        RegistrationNotificationService? notifications = null)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
        _notifications = notifications;
    }

    // ---------------------------------------------------------------- applicant

    /// <summary>
    /// Submits an application for each child, in one save. Refuses a child whose name the caller
    /// already has as an open or approved application or as a linked student, or appears twice in
    /// the same submission. <paramref name="chapterId"/> is the chapter the parent chose (FR-021,
    /// docs/adr/0010-chapters.md): it must be an open chapter, and that chapter's Safeguarding
    /// Officer reviews the application and its Admin approves it.
    /// </summary>
    public async Task<IReadOnlyList<StudentApplication>> SubmitAsync(
        int chapterId, ApplicantDetails applicant, IReadOnlyList<ChildApplicationForm> children, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(applicant);
        ArgumentNullException.ThrowIfNull(children);
        var userId = await RequireApplicantAsync(ct);

        if (!await _db.Chapters.AnyAsync(c => c.Id == chapterId && c.IsActive, ct))
        {
            throw new ArgumentException("Choose the chapter at your parish.");
        }

        if (children.Count == 0)
        {
            throw new ArgumentException("Add at least one child to the application.");
        }

        var today = Today();
        ValidateApplicant(applicant);
        foreach (var child in children)
        {
            ValidateChild(child, today);
        }

        var taken = await NamesAlreadyHeldAsync(userId, excludeApplicationId: null, ct);
        foreach (var child in children)
        {
            if (!taken.Add(NormaliseName(child.ChildName)))
            {
                throw new InvalidOperationException(
                    $"You have already registered or applied for a child called {child.ChildName.Trim()}. Each child can only be registered once.");
            }
        }

        var now = DateTime.UtcNow;
        var nextId = await _db.NextIdAsync<StudentApplication>(ct);
        var created = new List<StudentApplication>();
        foreach (var child in children)
        {
            var id = nextId++;
            var application = new StudentApplication
            {
                Id = id,
                ApplicantUserId = userId,
                ChapterId = chapterId,
                ParentPhone = applicant.Phone.Trim(),
                ParentPostalAddress = applicant.PostalAddress.Trim(),
                Relationship = child.Relationship.Trim(),
                ChildName = CollapseWhitespace(child.ChildName),
                ChildDateOfBirth = child.DateOfBirth,
                PhotoMediaConsent = child.PhotoMediaConsent,
                TransportConsent = child.TransportConsent,
                OffsiteTripConsent = child.OffsiteTripConsent,
                DataSharingConsent = child.DataSharingConsent,
                Status = StudentApplicationStatus.AwaitingSafeguardingReview,
                SubmittedAt = now,
                LastSubmittedAt = now
            };
            _db.StudentApplications.Add(application);

            // Id = the application's own id, never MAX(id)+1 over this table: that MAX runs through
            // the caller's query filter (an applicant sees only their own rows), the same reason as
            // ConsentRecordService.DetailsIdFor.
            _db.StudentApplicationSensitiveDetails.Add(new StudentApplicationSensitiveDetails
            {
                Id = id,
                StudentApplicationId = id,
                ChapterId = chapterId,
                EmergencyContact = child.EmergencyContact.Trim(),
                EmergencyMedicalInfo = OrNull(child.EmergencyMedicalInfo),
                DietaryRequirements = OrNull(child.DietaryRequirements),
                SpecialEducationalNeeds = OrNull(child.SpecialEducationalNeeds)
            });
            created.Add(application);
        }

        await _db.SaveChangesAsync(ct);

        if (_notifications is not null)
        {
            await _notifications.NotifySubmittedAsync(created.Select(a => a.Id).ToList(), resubmitted: false, ct);
        }

        return created;
    }

    /// <summary>The caller's own applications, newest first, with their restricted details (audited).</summary>
    public async Task<IReadOnlyList<ApplicationView>> GetMyApplicationsAsync(CancellationToken ct = default)
    {
        var userId = await RequireApplicantAsync(ct);
        var ids = await _db.StudentApplications
            .Where(a => a.ApplicantUserId == userId)
            .Select(a => a.Id)
            .ToListAsync(ct);
        return await LoadViewsAsync(ids, includeDetails: true, ct);
    }

    /// <summary>
    /// The applicant's answer to a request for more information: replaces the application's fields
    /// with <paramref name="applicant"/> and <paramref name="child"/>, records <paramref name="response"/>,
    /// and returns it to the Safeguarding Officer. Only the applicant, and only while more
    /// information is requested.
    /// </summary>
    public async Task<StudentApplication> ResubmitAsync(
        int applicationId, ApplicantDetails applicant, ChildApplicationForm child, string response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(applicant);
        ArgumentNullException.ThrowIfNull(child);
        var userId = await RequireApplicantAsync(ct);

        var application = await _db.StudentApplications.SingleOrDefaultAsync(a => a.Id == applicationId, ct);
        if (application is null || application.ApplicantUserId != userId)
        {
            throw new UnauthorizedAccessException("You can only change your own applications.");
        }

        if (application.Status != StudentApplicationStatus.MoreInformationRequested)
        {
            throw new InvalidOperationException("This application is not waiting for more information from you.");
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            throw new ArgumentException("Please answer the question before resubmitting.");
        }

        RequireWithin(response, ConsentSensitiveDetails.MaxTextLength, "Your answer");
        ValidateApplicant(applicant);
        ValidateChild(child, Today());

        var taken = await NamesAlreadyHeldAsync(userId, excludeApplicationId: applicationId, ct);
        if (taken.Contains(NormaliseName(child.ChildName)))
        {
            throw new InvalidOperationException(
                $"You have already registered or applied for a child called {child.ChildName.Trim()}. Each child can only be registered once.");
        }

        var details = await _db.StudentApplicationSensitiveDetails.SingleOrDefaultAsync(d => d.StudentApplicationId == applicationId, ct)
            ?? throw new UnauthorizedAccessException("You can only change your own applications.");

        var values = _db.Entry(application).CurrentValues;
        values[nameof(StudentApplication.ParentPhone)] = applicant.Phone.Trim();
        values[nameof(StudentApplication.ParentPostalAddress)] = applicant.PostalAddress.Trim();
        values[nameof(StudentApplication.Relationship)] = child.Relationship.Trim();
        values[nameof(StudentApplication.ChildName)] = CollapseWhitespace(child.ChildName);
        values[nameof(StudentApplication.ChildDateOfBirth)] = child.DateOfBirth;
        values[nameof(StudentApplication.PhotoMediaConsent)] = child.PhotoMediaConsent;
        values[nameof(StudentApplication.TransportConsent)] = child.TransportConsent;
        values[nameof(StudentApplication.OffsiteTripConsent)] = child.OffsiteTripConsent;
        values[nameof(StudentApplication.DataSharingConsent)] = child.DataSharingConsent;
        values[nameof(StudentApplication.Status)] = StudentApplicationStatus.AwaitingSafeguardingReview;
        values[nameof(StudentApplication.LastSubmittedAt)] = DateTime.UtcNow;

        var detailValues = _db.Entry(details).CurrentValues;
        detailValues[nameof(StudentApplicationSensitiveDetails.EmergencyContact)] = child.EmergencyContact.Trim();
        detailValues[nameof(StudentApplicationSensitiveDetails.EmergencyMedicalInfo)] = OrNull(child.EmergencyMedicalInfo);
        detailValues[nameof(StudentApplicationSensitiveDetails.DietaryRequirements)] = OrNull(child.DietaryRequirements);
        detailValues[nameof(StudentApplicationSensitiveDetails.SpecialEducationalNeeds)] = OrNull(child.SpecialEducationalNeeds);
        detailValues[nameof(StudentApplicationSensitiveDetails.ParentResponse)] = response.Trim();

        await _db.SaveChangesAsync(ct);

        if (_notifications is not null)
        {
            await _notifications.NotifySubmittedAsync([applicationId], resubmitted: true, ct);
        }

        return application;
    }

    // ---------------------------------------------------------------- stage 1: Safeguarding Officer

    /// <summary>Applications at stage 1 (waiting for review, or waiting for the parent's answer), oldest first. No restricted details.</summary>
    public async Task<IReadOnlyList<ApplicationView>> ListForSafeguardingAsync(CancellationToken ct = default)
    {
        RequireSafeguardingOfficer();
        var ids = await StaffApplications
            .Where(a => a.Status == StudentApplicationStatus.AwaitingSafeguardingReview
                || a.Status == StudentApplicationStatus.MoreInformationRequested)
            .Select(a => a.Id)
            .ToListAsync(ct);
        return (await LoadViewsAsync(ids, includeDetails: false, ct)).OrderBy(a => a.LastSubmittedAt).ToList();
    }

    /// <summary>Number waiting for stage-1 review, for the home page.</summary>
    public async Task<int> CountAwaitingSafeguardingReviewAsync(CancellationToken ct = default)
    {
        RequireSafeguardingOfficer();
        return await StaffApplications.CountAsync(a => a.Status == StudentApplicationStatus.AwaitingSafeguardingReview, ct);
    }

    /// <summary>One application with its restricted details (audited), for the Safeguarding Officer.</summary>
    public async Task<ApplicationView> GetForSafeguardingReviewAsync(int applicationId, CancellationToken ct = default)
    {
        RequireSafeguardingOfficer();
        await RequireInActiveChapterAsync(applicationId, ct);
        return await LoadViewAsync(applicationId, ct);
    }

    /// <summary>Stage 1 approval: passes the application to the Admins.</summary>
    public async Task SafeguardingApproveAsync(int applicationId, string? comments = null, CancellationToken ct = default)
    {
        var reviewerId = RequireSafeguardingOfficer();
        var application = await LoadAtStatusAsync(applicationId, StudentApplicationStatus.AwaitingSafeguardingReview, ct);
        RequireWithin(comments, ConsentSensitiveDetails.MaxTextLength, "Comments");

        SetStageOne(application, reviewerId, StudentApplicationStatus.AwaitingAdminApproval);
        if (!string.IsNullOrWhiteSpace(comments))
        {
            var details = await RequireDetailsAsync(applicationId, ct);
            _db.Entry(details).CurrentValues[nameof(StudentApplicationSensitiveDetails.DecisionComments)] = comments.Trim();
        }

        await _db.SaveChangesAsync(ct);

        if (_notifications is not null)
        {
            await _notifications.NotifyPassedSafeguardingAsync(applicationId, ct);
        }
    }

    /// <summary>Stage 1: asks the parent for more information. The request is required and is shown to the parent.</summary>
    public async Task RequestMoreInformationAsync(int applicationId, string request, CancellationToken ct = default)
    {
        var reviewerId = RequireSafeguardingOfficer();
        if (string.IsNullOrWhiteSpace(request))
        {
            throw new ArgumentException("Say what information you need from the parent.");
        }

        RequireWithin(request, ConsentSensitiveDetails.MaxTextLength, "The request");
        var application = await LoadAtStatusAsync(applicationId, StudentApplicationStatus.AwaitingSafeguardingReview, ct);
        var details = await RequireDetailsAsync(applicationId, ct);

        SetStageOne(application, reviewerId, StudentApplicationStatus.MoreInformationRequested);
        var detailValues = _db.Entry(details).CurrentValues;
        detailValues[nameof(StudentApplicationSensitiveDetails.InformationRequest)] = request.Trim();
        detailValues[nameof(StudentApplicationSensitiveDetails.ParentResponse)] = null;
        await _db.SaveChangesAsync(ct);

        if (_notifications is not null)
        {
            await _notifications.NotifyMoreInformationRequestedAsync(applicationId, ct);
        }
    }

    /// <summary>Stage 1 rejection, with mandatory comments shown to the parent.</summary>
    public async Task SafeguardingRejectAsync(int applicationId, string comments, CancellationToken ct = default)
    {
        var reviewerId = RequireSafeguardingOfficer();
        RequireRejectionComments(comments);
        var application = await LoadAtStatusAsync(applicationId, StudentApplicationStatus.AwaitingSafeguardingReview, ct);
        var details = await RequireDetailsAsync(applicationId, ct);

        SetStageOne(application, reviewerId, StudentApplicationStatus.Rejected);
        _db.Entry(details).CurrentValues[nameof(StudentApplicationSensitiveDetails.DecisionComments)] = comments.Trim();
        await _db.SaveChangesAsync(ct);

        if (_notifications is not null)
        {
            await _notifications.NotifyRejectedAsync(applicationId, ct);
        }
    }

    // ---------------------------------------------------------------- stage 2: Admin

    /// <summary>Applications waiting for final approval, oldest first. No restricted details.</summary>
    public async Task<IReadOnlyList<ApplicationView>> ListForAdminAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        var ids = await StaffApplications
            .Where(a => a.Status == StudentApplicationStatus.AwaitingAdminApproval)
            .Select(a => a.Id)
            .ToListAsync(ct);
        return (await LoadViewsAsync(ids, includeDetails: false, ct)).OrderBy(a => a.SafeguardingReviewedAt).ToList();
    }

    /// <summary>Number waiting for final approval, for the home page.</summary>
    public async Task<int> CountAwaitingAdminApprovalAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        return await StaffApplications.CountAsync(a => a.Status == StudentApplicationStatus.AwaitingAdminApproval, ct);
    }

    /// <summary>One application (with restricted details, audited), the existing students with the same name and date of birth, and the ranks.</summary>
    public async Task<ApplicationForApproval> GetForApprovalAsync(int applicationId, CancellationToken ct = default)
    {
        RequireAdmin();
        await RequireInActiveChapterAsync(applicationId, ct);
        var view = await LoadViewAsync(applicationId, ct);
        var matches = await FindSameNameAndDateOfBirthAsync(view.ChildName, view.ChildDateOfBirth, ct);
        var ranks = await _db.Ranks.OrderBy(r => r.Order).Select(r => new RankOption(r.Id, r.Order, r.Name)).ToListAsync(ct);
        return new ApplicationForApproval(view, matches, ranks);
    }

    /// <summary>
    /// Final approval. Exactly one of <paramref name="startingRankId"/> (create a new student at
    /// that rank) or <paramref name="existingStudentId"/> (link this parent to an existing student
    /// with the same name and date of birth) must be given. Everything is created in one
    /// transaction; see the class comment. Returns the student's id.
    /// </summary>
    public async Task<int> ApproveAsync(
        int applicationId, int? startingRankId, int? existingStudentId = null, CancellationToken ct = default)
    {
        var adminId = RequireAdmin();
        if ((startingRankId is null) == (existingStudentId is null))
        {
            throw new ArgumentException("Choose a starting rank for a new student, or an existing student to link to, but not both.");
        }

        var application = await LoadAtStatusAsync(applicationId, StudentApplicationStatus.AwaitingAdminApproval, ct);
        var details = await RequireDetailsAsync(applicationId, ct);

        var applicant = await _db.AppUsers.SingleAsync(u => u.Id == application.ApplicantUserId, ct);
        if (applicant.HasLeft)
        {
            throw new InvalidOperationException("The applicant has been marked as having left, so the application cannot be approved.");
        }

        if (await _db.Students.AnyAsync(s => s.AppUserId == applicant.Id, ct))
        {
            throw new InvalidOperationException("The applicant's account is a student sign-in and cannot be made a parent.");
        }

        // Duplicate rule again at approval: a student with this name may have been linked to the
        // applicant since they applied (for example by an Admin registering the child directly).
        var childKey = NormaliseName(application.ChildName);
        var linkedNames = await LinkedStudentNamesAsync(applicant.Id, ct);
        if (linkedNames.Contains(childKey))
        {
            throw new InvalidOperationException(
                $"{applicant.DisplayName} is already linked to a student called {application.ChildName}. Reject this application as a duplicate.");
        }

        var today = Today();
        var now = DateTime.UtcNow;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        Student student;
        if (existingStudentId is int linkId)
        {
            student = await _db.Students.SingleOrDefaultAsync(s => s.Id == linkId, ct)
                ?? throw new InvalidOperationException($"Student {linkId} does not exist.");
            if (NormaliseName(student.Name) != childKey || student.DateOfBirth != application.ChildDateOfBirth)
            {
                throw new InvalidOperationException(
                    "A parent can only be linked to an existing student with the same name and date of birth as the child on the application.");
            }
        }
        else
        {
            var rank = await _db.Ranks.SingleOrDefaultAsync(r => r.Id == startingRankId, ct)
                ?? throw new InvalidOperationException($"Rank {startingRankId} does not exist.");
            student = new Student
            {
                Id = await _db.NextIdAsync<Student>(ct),
                Name = application.ChildName,
                DateOfBirth = application.ChildDateOfBirth,
                CurrentRankId = rank.Id,
                Status = StudentStatus.Active,
                EnrolmentDate = today
            };
            _db.Students.Add(student);
            _db.StudentRankProgresses.Add(new StudentRankProgress
            {
                Id = await _db.NextIdAsync<StudentRankProgress>(ct),
                StudentId = student.Id,
                RankId = rank.Id,
                StartedAt = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                CompletedAt = null
            });
        }

        // The applicant's guardian record: their own, linked to their signed-in account. An
        // unlinked guardian record with the same email is deliberately not adopted: ownership is
        // proved by accepting that record's invite, never by matching the email alone (FR-004).
        var guardian = await _db.ParentGuardians.Where(g => g.AppUserId == applicant.Id).OrderBy(g => g.Id).FirstOrDefaultAsync(ct);
        if (guardian is null)
        {
            guardian = new ParentGuardian
            {
                Id = await _db.NextIdAsync<ParentGuardian>(ct),
                Name = applicant.DisplayName,
                ContactInfo = applicant.Email,
                IsPrimary = true,
                Phone = application.ParentPhone,
                PostalAddress = application.ParentPostalAddress,
                AppUserId = applicant.Id
            };
            _db.ParentGuardians.Add(guardian);
        }
        else
        {
            var guardianValues = _db.Entry(guardian).CurrentValues;
            guardianValues[nameof(ParentGuardian.Phone)] = application.ParentPhone;
            guardianValues[nameof(ParentGuardian.PostalAddress)] = application.ParentPostalAddress;
        }

        if (existingStudentId is not null
            && await _db.StudentGuardianLinks.AnyAsync(l => l.StudentId == student.Id && l.ParentGuardianId == guardian.Id, ct))
        {
            throw new InvalidOperationException($"{applicant.DisplayName} is already linked to {student.Name}.");
        }

        _db.StudentGuardianLinks.Add(new StudentGuardianLink
        {
            StudentId = student.Id,
            ParentGuardianId = guardian.Id,
            CreatedByUserId = adminId,
            Relationship = application.Relationship
        });

        // The consent form on the application, reviewed at stage 1, becomes the approved consent.
        // ApprovedByUserId is the Safeguarding Officer who reviewed it (consent sign-off is theirs,
        // never the Approver's); DecidedAt is the final approval.
        var periodEnd = ConsentPeriodEndFor(today);
        var consentId = await _db.NextIdAsync<ConsentRecord>(ct);
        _db.ConsentRecords.Add(new ConsentRecord
        {
            Id = consentId,
            StudentId = student.Id,
            PeriodStart = today,
            PeriodEnd = periodEnd,
            ExpiryDate = periodEnd,
            PhotoMediaConsent = application.PhotoMediaConsent,
            TransportConsent = application.TransportConsent,
            OffsiteTripConsent = application.OffsiteTripConsent,
            DataSharingConsent = application.DataSharingConsent,
            Status = ConsentStatus.Approved,
            SubmittedByParentId = guardian.Id,
            ApprovedByUserId = application.SafeguardingReviewedByUserId,
            // Read back from the database; SQLite returns no kind, PostgreSQL's timestamptz UTC.
            SubmittedAt = DateTime.SpecifyKind(application.LastSubmittedAt, DateTimeKind.Utc),
            SubmittedByUserId = applicant.Id,
            StudentName = application.ChildName,
            StudentDateOfBirth = application.ChildDateOfBirth,
            DecidedAt = now
        });
        _db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails
        {
            Id = ConsentRecordService.DetailsIdFor(consentId),
            ConsentRecordId = consentId,
            EmergencyContact = details.EmergencyContact,
            EmergencyMedicalInfo = details.EmergencyMedicalInfo,
            DietaryRequirements = details.DietaryRequirements,
            SpecialEducationalNeeds = details.SpecialEducationalNeeds
        });

        // Parent in the application's chapter (docs/adr/0010-chapters.md): a Parent role in another
        // chapter does not count here.
        var grantParent = !await _db.UserRoles.AnyAsync(
            r => r.UserId == applicant.Id && r.Role == Role.Parent && r.ChapterId == application.ChapterId, ct);
        if (grantParent)
        {
            _db.UserRoles.Add(new UserRole { UserId = applicant.Id, Role = Role.Parent, ChapterId = application.ChapterId });
        }

        var values = _db.Entry(application).CurrentValues;
        values[nameof(StudentApplication.Status)] = StudentApplicationStatus.Approved;
        values[nameof(StudentApplication.AdminDecidedByUserId)] = adminId;
        values[nameof(StudentApplication.AdminDecidedAt)] = now;
        values[nameof(StudentApplication.StudentId)] = student.Id;
        values[nameof(StudentApplication.LinkedToExistingStudent)] = existingStudentId is not null;
        values[nameof(StudentApplication.ConsentRecordId)] = consentId;

        await _db.SaveChangesAsync(ct);

        // FR-016: a role change takes effect at once and re-validates existing sign-ins, as in
        // RoleAssignmentService.GrantAsync. Inside the transaction, so a failure undoes everything.
        if (grantParent)
        {
            await SecurityStamps.RotateAsync(_userManager, applicant.Id);
        }

        await transaction.CommitAsync(ct);

        if (_notifications is not null)
        {
            await _notifications.NotifyApprovedAsync(applicationId, ct);
        }

        return student.Id;
    }

    /// <summary>Stage 2 rejection, with mandatory comments shown to the parent.</summary>
    public async Task AdminRejectAsync(int applicationId, string comments, CancellationToken ct = default)
    {
        var adminId = RequireAdmin();
        RequireRejectionComments(comments);
        var application = await LoadAtStatusAsync(applicationId, StudentApplicationStatus.AwaitingAdminApproval, ct);
        var details = await RequireDetailsAsync(applicationId, ct);

        var values = _db.Entry(application).CurrentValues;
        values[nameof(StudentApplication.Status)] = StudentApplicationStatus.Rejected;
        values[nameof(StudentApplication.AdminDecidedByUserId)] = adminId;
        values[nameof(StudentApplication.AdminDecidedAt)] = DateTime.UtcNow;
        _db.Entry(details).CurrentValues[nameof(StudentApplicationSensitiveDetails.DecisionComments)] = comments.Trim();
        await _db.SaveChangesAsync(ct);

        if (_notifications is not null)
        {
            await _notifications.NotifyRejectedAsync(applicationId, ct);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Consent period end for a consent approved on <paramref name="approvedOn"/>.
    /// SEAM (22/10/2026): this mirrors the rule <see cref="ParentConsentService.SubmitAsync"/> uses
    /// at this item's base (one year less a day). adr/0008 answer 2 changes consent to end on
    /// 31 August of the church year; master now has <c>Safeguarding.ConsentYear.EndFor(date)</c> for
    /// that. At merge, replace this body with <c>ConsentYear.EndFor(approvedOn)</c> so registration
    /// consent and parent-form consent agree.
    /// </summary>
    internal static DateOnly ConsentPeriodEndFor(DateOnly approvedOn) => Safeguarding.ConsentYear.EndFor(approvedOn);

    /// <summary>Case-insensitive, whitespace-collapsed form of a child's name, for the duplicate rule.</summary>
    internal static string NormaliseName(string name) => CollapseWhitespace(name).ToUpperInvariant();

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? OrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static void ValidateApplicant(ApplicantDetails applicant)
    {
        RequireText(applicant.Phone, "Your phone number", StudentApplication.MaxShortTextLength);
        RequireText(applicant.PostalAddress, "Your postal address", StudentApplication.MaxAddressLength);
    }

    private static void ValidateChild(ChildApplicationForm child, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(child);
        RequireText(child.ChildName, "The child's name", StudentApplication.MaxShortTextLength);
        RequireText(child.Relationship, $"Your relationship to {Display(child.ChildName)}", StudentApplication.MaxShortTextLength);
        RequireText(child.EmergencyContact, $"The emergency contact for {Display(child.ChildName)}", StudentApplication.MaxAddressLength);
        RequireWithin(child.EmergencyMedicalInfo, ConsentSensitiveDetails.MaxTextLength, "Medical information");
        RequireWithin(child.DietaryRequirements, ConsentSensitiveDetails.MaxTextLength, "Dietary requirements");
        RequireWithin(child.SpecialEducationalNeeds, ConsentSensitiveDetails.MaxTextLength, "Special educational needs");
        if (child.DateOfBirth > today)
        {
            throw new ArgumentException($"The date of birth for {Display(child.ChildName)} cannot be in the future.");
        }

        if (child.DateOfBirth == default)
        {
            throw new ArgumentException($"Enter the date of birth for {Display(child.ChildName)}.");
        }
    }

    private static string Display(string? name) => string.IsNullOrWhiteSpace(name) ? "the child" : name.Trim();

    private static void RequireText(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{field} is required.");
        }

        RequireWithin(value, maxLength, field);
    }

    private static void RequireWithin(string? value, int maxLength, string field)
    {
        if (value is not null && value.Trim().Length > maxLength)
        {
            throw new ArgumentException($"{field} must be {maxLength} characters or fewer.");
        }
    }

    private static void RequireRejectionComments(string? comments)
    {
        if (string.IsNullOrWhiteSpace(comments))
        {
            throw new ArgumentException("Comments are required when rejecting an application, so the parent knows why.");
        }

        RequireWithin(comments, ConsentSensitiveDetails.MaxTextLength, "Comments");
    }

    private void SetStageOne(StudentApplication application, int reviewerId, StudentApplicationStatus status)
    {
        var values = _db.Entry(application).CurrentValues;
        values[nameof(StudentApplication.Status)] = status;
        values[nameof(StudentApplication.SafeguardingReviewedByUserId)] = reviewerId;
        values[nameof(StudentApplication.SafeguardingReviewedAt)] = DateTime.UtcNow;
    }

    private async Task<StudentApplication> LoadAtStatusAsync(int applicationId, StudentApplicationStatus expected, CancellationToken ct)
    {
        var application = await StaffApplications.SingleOrDefaultAsync(a => a.Id == applicationId, ct)
            ?? throw new InvalidOperationException($"Application {applicationId} does not exist.");
        if (application.Status != expected)
        {
            throw new InvalidOperationException(
                $"This application is {Describe(application.Status)}, so it cannot be decided at this stage.");
        }

        return application;
    }

    /// <summary>
    /// The applications a reviewing Safeguarding Officer or Admin works on: their active chapter's
    /// only (FR-021, docs/adr/0010-chapters.md). The chapter query filter also lets an applicant see
    /// their own applications in any chapter, so a member of staff who has applied elsewhere would
    /// otherwise find their own application in their review lists.
    /// </summary>
    private IQueryable<StudentApplication> StaffApplications =>
        _db.StudentApplications.Where(a => a.ChapterId == _currentUser.ActiveChapterId);

    private async Task RequireInActiveChapterAsync(int applicationId, CancellationToken ct)
    {
        if (!await StaffApplications.AnyAsync(a => a.Id == applicationId, ct))
        {
            throw new InvalidOperationException($"Application {applicationId} does not exist.");
        }
    }

    /// <summary>Reads the restricted row through the query filter (audited). Null means the caller may not read it.</summary>
    private async Task<StudentApplicationSensitiveDetails> RequireDetailsAsync(int applicationId, CancellationToken ct) =>
        await _db.StudentApplicationSensitiveDetails.SingleOrDefaultAsync(d => d.StudentApplicationId == applicationId, ct)
            ?? throw new UnauthorizedAccessException("You cannot read this application's details. Two-factor authentication must be turned on.");

    /// <summary>Plain-English status, for messages and pages.</summary>
    public static string Describe(StudentApplicationStatus status) => status switch
    {
        StudentApplicationStatus.AwaitingSafeguardingReview => "waiting for safeguarding review",
        StudentApplicationStatus.MoreInformationRequested => "waiting for more information from the parent",
        StudentApplicationStatus.AwaitingAdminApproval => "waiting for final approval",
        StudentApplicationStatus.Approved => "approved",
        StudentApplicationStatus.Rejected => "rejected",
        _ => status.ToString()
    };

    private async Task<HashSet<string>> NamesAlreadyHeldAsync(int userId, int? excludeApplicationId, CancellationToken ct)
    {
        var applicationNames = await _db.StudentApplications
            .Where(a => a.ApplicantUserId == userId
                && a.Status != StudentApplicationStatus.Rejected
                && (excludeApplicationId == null || a.Id != excludeApplicationId))
            .Select(a => a.ChildName)
            .ToListAsync(ct);

        var names = await LinkedStudentNamesAsync(userId, ct);
        foreach (var name in applicationNames)
        {
            names.Add(NormaliseName(name));
        }

        return names;
    }

    private async Task<HashSet<string>> LinkedStudentNamesAsync(int userId, CancellationToken ct)
    {
        var studentNames = await _db.StudentGuardianLinks
            .Where(l => l.ParentGuardian!.AppUserId == userId)
            .Select(l => l.Student!.Name)
            .ToListAsync(ct);
        return studentNames.Select(NormaliseName).ToHashSet();
    }

    private async Task<IReadOnlyList<ExistingStudentMatch>> FindSameNameAndDateOfBirthAsync(
        string childName, DateOnly dateOfBirth, CancellationToken ct)
    {
        var key = NormaliseName(childName);
        var candidates = await _db.Students
            .Where(s => s.DateOfBirth == dateOfBirth)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.DateOfBirth,
                s.Status,
                RankName = s.CurrentRank!.Name,
                Guardians = s.GuardianLinks.Select(l => l.ParentGuardian!.Name).ToList()
            })
            .ToListAsync(ct);

        return candidates
            .Where(c => NormaliseName(c.Name) == key)
            .OrderBy(c => c.Id)
            .Select(c => new ExistingStudentMatch(c.Id, c.Name, c.DateOfBirth, c.Status, c.RankName, c.Guardians.Order().ToList()))
            .ToList();
    }

    private async Task<ApplicationView> LoadViewAsync(int applicationId, CancellationToken ct)
    {
        var views = await LoadViewsAsync([applicationId], includeDetails: true, ct);
        return views.SingleOrDefault() ?? throw new InvalidOperationException($"Application {applicationId} does not exist.");
    }

    private async Task<IReadOnlyList<ApplicationView>> LoadViewsAsync(
        IReadOnlyCollection<int> ids, bool includeDetails, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await _db.StudentApplications
            .Where(a => ids.Contains(a.Id))
            .Select(a => new
            {
                Application = a,
                ApplicantName = a.ApplicantUser!.DisplayName,
                ApplicantEmail = a.ApplicantUser.Email,
                ReviewerName = a.SafeguardingReviewedByUser == null ? null : a.SafeguardingReviewedByUser.DisplayName,
                AdminName = a.AdminDecidedByUser == null ? null : a.AdminDecidedByUser.DisplayName
            })
            .ToListAsync(ct);

        // Whole entities through the query filter, so each row the caller may read is audited and
        // rows they may not read are simply absent.
        var details = includeDetails
            ? (await _db.StudentApplicationSensitiveDetails.Where(d => ids.Contains(d.StudentApplicationId)).ToListAsync(ct))
                .ToDictionary(d => d.StudentApplicationId)
            : [];

        return rows
            .OrderByDescending(r => r.Application.SubmittedAt)
            .ThenByDescending(r => r.Application.Id)
            .Select(r =>
            {
                var a = r.Application;
                var d = details.GetValueOrDefault(a.Id);
                return new ApplicationView(
                    a.Id, a.Status, a.ApplicantUserId, r.ApplicantName, r.ApplicantEmail, a.ParentPhone, a.ParentPostalAddress,
                    a.Relationship, a.ChildName, a.ChildDateOfBirth, a.PhotoMediaConsent, a.TransportConsent,
                    a.OffsiteTripConsent, a.DataSharingConsent, a.SubmittedAt, a.LastSubmittedAt, r.ReviewerName,
                    a.SafeguardingReviewedAt, r.AdminName, a.AdminDecidedAt, a.StudentId, a.LinkedToExistingStudent,
                    d is null
                        ? null
                        : new ApplicationDetailsView(d.EmergencyContact, d.EmergencyMedicalInfo, d.DietaryRequirements,
                            d.SpecialEducationalNeeds, d.InformationRequest, d.ParentResponse, d.DecisionComments));
            })
            .ToList();
    }

    /// <summary>
    /// Any signed-in adult may apply, holding no role yet: not a student sign-in, and not someone
    /// marked as having left.
    /// </summary>
    private async Task<int> RequireApplicantAsync(CancellationToken ct)
    {
        if (_currentUser.UserId is not int userId)
        {
            throw new UnauthorizedAccessException("Sign in to apply to register a child.");
        }

        if (_currentUser.HasRole(Role.Student) || await _db.Students.AnyAsync(s => s.AppUserId == userId, ct))
        {
            throw new UnauthorizedAccessException("A student sign-in cannot apply to register a child. Please ask your parent.");
        }

        var hasLeft = await _db.AppUsers.Where(u => u.Id == userId).Select(u => (bool?)u.HasLeft).SingleOrDefaultAsync(ct);
        if (hasLeft is not false)
        {
            throw new UnauthorizedAccessException("Caller's account is not an active user.");
        }

        return userId;
    }

    /// <summary>Stage 1 is the Safeguarding Officer's, with MFA (the same condition as the restricted-data query filter).</summary>
    private int RequireSafeguardingOfficer()
    {
        if (!_currentUser.HasRole(Role.SafeguardingOfficer) || !_currentUser.HasMfaEnabled || _currentUser.UserId is not int userId)
        {
            throw new UnauthorizedAccessException(
                "Only a Safeguarding Officer with two-factor authentication turned on can review registration applications.");
        }

        return userId;
    }

    /// <summary>Stage 2 is the Admin's, with MFA.</summary>
    private int RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin) || !_currentUser.HasMfaEnabled || _currentUser.UserId is not int userId)
        {
            throw new UnauthorizedAccessException(
                "Only an Admin with two-factor authentication turned on can give final approval to registration applications.");
        }

        return userId;
    }
}
