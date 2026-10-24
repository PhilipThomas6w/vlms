using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Safeguarding;

namespace Vlms.Infrastructure.Chapters;

/// <summary>What <see cref="DevelopmentChapterSeeder.SeedAsync"/> created (or found already there).</summary>
public sealed record DevelopmentSeedResult(int ChapterId, bool ChapterCreated, IReadOnlyList<string> AccountsCreated);

/// <summary>
/// Development data for testing chapter separation by hand (Philip, 24/10/2026; FR-021,
/// docs/adr/0010-chapters.md): a second chapter, "The Greek Orthodox Church of the Holy
/// Transfiguration, Coventry", with its own Admin, Teacher, Safeguarding Officer and parent
/// accounts, a student (with a sign-in) linked to that parent, and the student's approved consent.
/// Run as <c>Vlms.Jobs seed-dev-chapters --password &lt;password&gt;</c>, which refuses outside the
/// Development environment; every account gets that one password and a confirmed address under
/// <c>example.test</c> (a reserved name, so no mail can reach a real person). Idempotent: anything
/// already there by name, email or username is left alone. Runs as the system principal, which may
/// file records in a named chapter; nothing here is reachable from Vlms.Web.
/// </summary>
public sealed class DevelopmentChapterSeeder
{
    public const string CoventryName = "The Greek Orthodox Church of the Holy Transfiguration, Coventry";
    public const string StudentUsername = "coventry.student";

    public static readonly IReadOnlyList<(string Email, string Name, Role Role)> CoventryStaff =
    [
        ("coventry.admin@example.test", "Coventry Admin", Role.Admin),
        ("coventry.teacher@example.test", "Coventry Teacher", Role.Teacher),
        ("coventry.safeguarding@example.test", "Coventry Safeguarding Officer", Role.SafeguardingOfficer),
        ("coventry.parent@example.test", "Coventry Parent", Role.Parent)
    ];

    private readonly VlmsDbContext _db;
    private readonly AppUserProvisioningService _provisioning;

    public DevelopmentChapterSeeder(VlmsDbContext db, AppUserProvisioningService provisioning)
    {
        _db = db;
        _provisioning = provisioning;
    }

    public async Task<DevelopmentSeedResult> SeedAsync(string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var rank = await _db.Ranks.OrderBy(r => r.Order).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No ranks exist yet: run seed-curriculum first.");

        var chapter = await _db.Chapters.SingleOrDefaultAsync(c => c.Name == CoventryName, ct);
        var chapterCreated = chapter is null;
        if (chapter is null)
        {
            chapter = new Chapter
            {
                Id = await _db.NextIdAsync<Chapter>(ct), Name = CoventryName, Place = "Coventry",
                CreatedAt = DateTime.UtcNow, IsActive = true
            };
            _db.Chapters.Add(chapter);
            await _db.SaveChangesAsync(ct);
        }

        var created = new List<string>();
        var ids = new Dictionary<Role, int>();
        foreach (var (email, name, role) in CoventryStaff)
        {
            var id = await _provisioning.FindAdultAccountIdByEmailAsync(email);
            if (id is null)
            {
                var (result, _, appUser) = await _provisioning.CreateAsync(email, password, name, ct, emailConfirmed: true);
                if (!result.Succeeded || appUser is null)
                {
                    throw new InvalidOperationException(
                        $"Could not create {email}: " + string.Join(" ", result.Errors.Select(e => e.Description)));
                }

                id = appUser.Id;
                created.Add(email);
            }

            ids[role] = id.Value;
            if (!await _db.UserRoles.AnyAsync(r => r.UserId == id && r.Role == role && r.ChapterId == chapter.Id, ct))
            {
                _db.UserRoles.Add(new UserRole { UserId = id.Value, Role = role, ChapterId = chapter.Id });
                await _db.SaveChangesAsync(ct);
            }
        }

        if (!await _db.Students.AnyAsync(s => s.ChapterId == chapter.Id, ct))
        {
            await SeedStudentAsync(chapter.Id, rank.Id, ids[Role.Parent], ids[Role.Admin], ids[Role.SafeguardingOfficer], password, created, ct);
        }

        return new DevelopmentSeedResult(chapter.Id, chapterCreated, created);
    }

    private async Task SeedStudentAsync(
        int chapterId, int rankId, int parentUserId, int adminUserId, int officerUserId, string password,
        List<string> created, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var student = new Student
        {
            Id = await _db.NextIdAsync<Student>(ct), ChapterId = chapterId, Name = "Coventry Test Student",
            DateOfBirth = today.AddYears(-10), CurrentRankId = rankId, Status = StudentStatus.Active, EnrolmentDate = today
        };
        var guardian = new ParentGuardian
        {
            Id = await _db.NextIdAsync<ParentGuardian>(ct), ChapterId = chapterId, Name = "Coventry Parent",
            ContactInfo = "coventry.parent@example.test", IsPrimary = true, AppUserId = parentUserId
        };
        _db.Students.Add(student);
        _db.ParentGuardians.Add(guardian);
        _db.StudentRankProgresses.Add(new StudentRankProgress
        {
            Id = await _db.NextIdAsync<StudentRankProgress>(ct), StudentId = student.Id, RankId = rankId,
            StartedAt = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
        });
        _db.StudentGuardianLinks.Add(new StudentGuardianLink
        {
            StudentId = student.Id, ParentGuardianId = guardian.Id, CreatedByUserId = adminUserId, Relationship = "Parent"
        });

        var consentId = await _db.NextIdAsync<ConsentRecord>(ct);
        var expiry = ConsentYear.EndFor(today);
        _db.ConsentRecords.Add(new ConsentRecord
        {
            Id = consentId, StudentId = student.Id, PeriodStart = today, PeriodEnd = expiry, ExpiryDate = expiry,
            PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
            Status = ConsentStatus.Approved, SubmittedByParentId = guardian.Id, ApprovedByUserId = officerUserId,
            StudentName = student.Name, StudentDateOfBirth = student.DateOfBirth, DecidedAt = DateTime.UtcNow
        });
        _db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails
        {
            Id = ConsentRecordService.DetailsIdFor(consentId), ConsentRecordId = consentId,
            EmergencyContact = "Coventry Parent, 07000 000000 (test data)"
        });
        await _db.SaveChangesAsync(ct);

        if (await _provisioning.FindAdultAccountIdByEmailAsync(StudentUsername) is null)
        {
            var (result, _, appUser) = await _provisioning.CreateStudentSignInAsync(StudentUsername, password, student.Name, ct);
            if (!result.Succeeded || appUser is null)
            {
                throw new InvalidOperationException(
                    $"Could not create the student sign-in: " + string.Join(" ", result.Errors.Select(e => e.Description)));
            }

            _db.UserRoles.Add(new UserRole { UserId = appUser.Id, Role = Role.Student, ChapterId = chapterId });
            _db.Entry(student).CurrentValues[nameof(Student.AppUserId)] = appUser.Id;
            await _db.SaveChangesAsync(ct);
            created.Add(StudentUsername);
        }
    }
}
