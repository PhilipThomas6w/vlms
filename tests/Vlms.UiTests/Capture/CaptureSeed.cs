using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Identity;

namespace Vlms.UiTests.Capture;

/// <summary>A signed-in persona for the capture run. <see cref="AuthenticatorKey"/> is set for two-factor accounts.</summary>
public sealed record CapturePersona(string Name, string UserName, string? AuthenticatorKey, int UserId);

/// <summary>
/// Synthetic, realistic data for screenshots: the real rank ladder and lesson titles from
/// <c>seed/curriculum.json</c>, students spread across the ranks with completions, badges, certificates and
/// consent, and one account per role. Everything lives in the factory's in-memory database; no real person's data.
/// </summary>
public static class CaptureSeed
{
    public const string Password = "Capture-Horse-9!";

    private static readonly string[] StudentNames =
    [
        "Alexander Hughes", "Sophia Hughes", "Nicholas Price", "Helena Shaw", "Theodore Ward", "Irene Barker",
        "Constantine Fox", "Anastasia Reed", "Gabriel Hart", "Zoe Pemberton", "Matthew Lloyd", "Catherine Doyle",
        "Peter Clarke", "Mary Evans", "Andrew Shaw", "Lydia Morgan", "Simeon Bell", "Thecla Grant",
    ];

    // Rank order and lessons completed in it, per student above.
    private static readonly (int Rank, int Done)[] Placement =
    [
        (3, 5), (1, 2), (4, 7), (2, 8 - 1), (5, 3), (1, 0), (6, 1), (2, 4), (7, 6), (3, 0), (4, 2), (1, 7),
        (3, 4), (3, 6), (3, 5), (3, 2), (2, 3), (4, 1),
    ];

    public static async Task<IReadOnlyDictionary<string, CapturePersona>> SeedAsync(UiWebFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<VlmsDbContext>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        await using var db = new VlmsDbContext(options, new SeedUserContext());

        var nextUserId = 100;
        var personas = new Dictionary<string, CapturePersona>();

        async Task<CapturePersona> AddUser(string key, string display, string userName, string? email, bool totp, params Role[] roles)
        {
            var id = ++nextUserId;
            var identity = new VlmsIdentityUser { Id = id, UserName = userName, Email = email, EmailConfirmed = true };
            var created = await users.CreateAsync(identity, Password);
            if (!created.Succeeded) throw new InvalidOperationException(string.Join("; ", created.Errors.Select(e => e.Description)));
            string? authenticatorKey = null;
            if (totp)
            {
                await users.ResetAuthenticatorKeyAsync(identity);
                authenticatorKey = await users.GetAuthenticatorKeyAsync(identity);
                await users.SetTwoFactorEnabledAsync(identity, true);
            }

            db.AppUsers.Add(new AppUser { Id = id, DisplayName = display, Email = email ?? userName });
            foreach (var role in roles) db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = RoleScope.IsChapterRole(role) ? Chapter.ShrewsburyId : null });
            await db.SaveChangesAsync();
            var persona = new CapturePersona(key, userName, authenticatorKey, id);
            personas[key] = persona;
            return persona;
        }

        var admin = await AddUser("admin", "Margaret Admin", "admin@capture.test", "admin@capture.test", true, Role.Admin);
        var teacher = await AddUser("teacher", "Fr Daniel Teacher", "teacher@capture.test", "teacher@capture.test", true, Role.Teacher);
        await AddUser("approver", "Elizabeth Approver", "approver@capture.test", "approver@capture.test", false, Role.Approver);
        await AddUser("safeguarding", "Barbara Safeguarding", "safeguarding@capture.test", "safeguarding@capture.test", true, Role.SafeguardingOfficer);
        var parent = await AddUser("parent", "Rachel Hughes", "parent@capture.test", "parent@capture.test", false, Role.Parent);
        await AddUser("national", "Nina National", "national@capture.test", "national@capture.test", true, Role.NationalAdministrator);
        await AddUser("admin-teacher", "Philip Combined", "combined@capture.test", "combined@capture.test", true, Role.Admin, Role.Teacher, Role.SafeguardingOfficer, Role.Parent);

        // Curriculum: the real ladder and titles.
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(FindRepoFile("seed/curriculum.json")));
        var ranks = new List<Rank>();
        var lessonsByRank = new Dictionary<int, List<Lesson>>();
        var lessonId = 0;
        var fileId = 0;
        foreach (var r in manifest.RootElement.GetProperty("ranks").EnumerateArray())
        {
            var order = r.GetProperty("order").GetInt32();
            var rank = new Rank { Id = order, Order = order, Code = r.GetProperty("code").GetString()!, Name = r.GetProperty("name").GetString()! };
            ranks.Add(rank);
            db.Ranks.Add(rank);
            var list = new List<Lesson>();
            foreach (var l in r.GetProperty("lessons").EnumerateArray())
            {
                var lesson = new Lesson
                {
                    Id = ++lessonId, RankId = rank.Id, Code = l.GetProperty("code").GetString()!,
                    Title = l.GetProperty("title").GetString()!, Order = l.GetProperty("order").GetInt32(), IsActive = true,
                };
                list.Add(lesson);
                db.Lessons.Add(lesson);
                foreach (var tag in l.GetProperty("tags").EnumerateArray())
                {
                    db.LessonTagAssignments.Add(new LessonTagAssignment { LessonId = lesson.Id, Tag = Enum.Parse<LessonTag>(tag.GetString()!.Replace(" ", "")) });
                }

                db.LessonFiles.Add(new LessonFile { Id = ++fileId, LessonId = lesson.Id, Order = 1, FileName = $"{lesson.Title}.docx", BlobKey = $"capture/missing-{fileId}.docx" });
                if (lesson.Id % 3 == 0)
                {
                    db.LessonFiles.Add(new LessonFile { Id = ++fileId, LessonId = lesson.Id, Order = 2, FileName = $"{lesson.Title} - handout.pdf", BlobKey = $"capture/missing-{fileId}.pdf" });
                }
            }

            lessonsByRank[rank.Id] = list;
        }

        await db.SaveChangesAsync();

        // A plain placeholder badge per written rank (the real designs are uploaded by an Admin).
        var badges = new Dictionary<int, RankBadge>();
        foreach (var rank in ranks.Where(r => lessonsByRank[r.Id].Count > 0))
        {
            // VLMS_CAPTURE_BADGES: a local folder of rank-<order>.png images (for example photographs of the
            // real patches, never committed); otherwise a plain numbered placeholder.
            var photo = Environment.GetEnvironmentVariable("VLMS_CAPTURE_BADGES") is { Length: > 0 } dir
                ? Path.Combine(dir, $"rank-{rank.Order}.png")
                : null;
            string key;
            if (photo is not null && File.Exists(photo))
            {
                key = $"badges/capture-{rank.Id}.png";
                await blobs.UploadAsync(key, await File.ReadAllBytesAsync(photo));
            }
            else
            {
                key = $"badges/capture-{rank.Id}.svg";
                var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\"><circle cx=\"32\" cy=\"32\" r=\"30\" fill=\"#334\"/><text x=\"32\" y=\"40\" font-size=\"22\" text-anchor=\"middle\" fill=\"#fff\">{rank.Order}</text></svg>";
                await blobs.UploadAsync(key, Encoding.UTF8.GetBytes(svg));
            }
            var badge = new RankBadge { Id = rank.Id, RankId = rank.Id, ImageBlobKey = key };
            badges[rank.Id] = badge;
            db.RankBadges.Add(badge);
        }

        await db.SaveChangesAsync();

        // Guardians: the parent persona is guardian of the first two students.
        var guardian = new ParentGuardian { Id = 1, Name = "Rachel Hughes", ContactInfo = "parent@capture.test", IsPrimary = true, Phone = "07700 900123", AppUserId = parent.UserId };
        db.ParentGuardians.Add(guardian);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var expiry = new DateOnly(today.Month <= 8 ? today.Year : today.Year + 1, 8, 31);
        var completionId = 0;
        var certificateId = 0;
        var progressId = 0;
        var badgeAwardId = 0;
        var consentId = 0;

        for (var i = 0; i < StudentNames.Length; i++)
        {
            var (rankOrder, done) = Placement[i];
            var enrolled = today.AddMonths(-6 * rankOrder - 3);
            int? signInUserId = i < 2
                ? (await AddUser("student-" + (i + 1), StudentNames[i], i == 0 ? "alex.hughes" : "sophia.hughes", null, false, Role.Student)).UserId
                : null;
            var student = new Student
            {
                Id = i + 1, Name = StudentNames[i], DateOfBirth = today.AddYears(-8 - rankOrder - (i % 3)).AddDays(-40 * i),
                CurrentRankId = rankOrder, Status = StudentStatus.Active, EnrolmentDate = enrolled,
                AssignedTeacherUserId = i % 2 == 0 ? teacher.UserId : null, AppUserId = signInUserId,
            };
            db.Students.Add(student);
            await db.SaveChangesAsync();

            var at = enrolled.ToDateTime(new TimeOnly(10, 30), DateTimeKind.Utc);
            foreach (var rank in ranks.Where(r => r.Order <= rankOrder))
            {
                db.StudentRankProgresses.Add(new StudentRankProgress
                {
                    Id = ++progressId, StudentId = student.Id, RankId = rank.Id, StartedAt = at,
                    CompletedAt = rank.Order < rankOrder ? at.AddDays(7 * 8) : null,
                });
                var lessons = lessonsByRank[rank.Id];
                var count = rank.Order < rankOrder ? lessons.Count : Math.Min(done, lessons.Count);
                foreach (var lesson in lessons.OrderBy(l => l.Order).Take(count))
                {
                    at = at.AddDays(7);
                    var completion = new StudentLessonCompletion
                    {
                        Id = ++completionId, StudentId = student.Id, LessonId = lesson.Id, CompletedByUserId = teacher.UserId,
                        CompletedAt = at < now ? at : now.AddDays(-1), IsReversed = false,
                    };
                    db.StudentLessonCompletions.Add(completion);
                    db.Certificates.Add(new Certificate { Id = ++certificateId, StudentLessonCompletionId = completion.Id, GeneratedAt = completion.CompletedAt, BlobKey = $"capture/certificate-{certificateId}.pdf" });
                }

                if (rank.Order < rankOrder && badges.TryGetValue(rank.Id, out var rankBadge))
                {
                    db.StudentBadges.Add(new StudentBadge { Id = ++badgeAwardId, StudentId = student.Id, RankBadgeId = rankBadge.Id, AwardedAt = at });
                }
            }

            if (i < 2)
            {
                db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = student.Id, ParentGuardianId = guardian.Id, CreatedByUserId = admin.UserId, Relationship = "Mother" });
            }

            // Consent: approved and in force for most; student 6 has none (blocked on Record completions); student 10 waits for review.
            if (i != 5)
            {
                var pending = i == 9;
                var consent = new ConsentRecord
                {
                    Id = ++consentId, StudentId = student.Id, PeriodStart = expiry.AddYears(-1).AddDays(1), PeriodEnd = expiry,
                    PhotoMediaConsent = i % 4 != 0, TransportConsent = true, OffsiteTripConsent = i % 3 != 0, DataSharingConsent = true,
                    Status = pending ? ConsentStatus.Pending : ConsentStatus.Approved, SubmittedByParentId = guardian.Id,
                    ApprovedByUserId = pending ? null : admin.UserId, ExpiryDate = expiry, SubmittedAt = now.AddDays(-20 - i),
                    StudentName = student.Name, StudentDateOfBirth = student.DateOfBirth, DecidedAt = pending ? null : now.AddDays(-18 - i),
                };
                db.ConsentRecords.Add(consent);
                db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails
                {
                    Id = consentId, ConsentRecordId = consent.Id,
                    EmergencyMedicalInfo = i % 3 == 0 ? "Mild asthma; inhaler in bag." : null,
                    DietaryRequirements = i % 4 == 1 ? "No nuts." : null,
                    SpecialEducationalNeeds = null,
                    EmergencyContact = "Parent: 07700 900" + (100 + i),
                });
            }

            await db.SaveChangesAsync();
        }

        // Meeting days and registers (FR-020, FR-022): monthly meetings, today's the latest, one
        // register per class (rank). Student 3 missed the last two (flagged at risk); student 6 (no
        // consent) attended today. One meeting day is still to come.
        var markId = 0;
        var registerId = 0;
        for (var m = 0; m < 4; m++)
        {
            var meeting = today.AddDays(-28 * m);
            var chapterEvent = new ChapterEvent
            {
                Id = m + 1, Date = meeting, Title = "Chapter meeting", StartTime = new TimeOnly(14, 0), EndTime = new TimeOnly(16, 0),
                Location = "Church hall", CreatedByUserId = teacher.UserId, CreatedAt = now.AddDays(-28 * m - 30),
            };
            db.ChapterEvents.Add(chapterEvent);
            foreach (var rankId in Placement.Select(p => p.Rank).Distinct().Order())
            {
                var register = new AttendanceRegister
                {
                    Id = ++registerId, ChapterEventId = chapterEvent.Id, RankId = rankId, MeetingDate = meeting,
                    TakenByUserId = teacher.UserId, TakenAt = now.AddDays(-28 * m),
                };
                db.AttendanceRegisters.Add(register);
                for (var i = 0; i < StudentNames.Length; i++)
                {
                    if (Placement[i].Rank != rankId) continue;
                    var present = !(i == 2 && m < 2) && !(i == 7 && m == 1) && !(i == 5 && m > 0);
                    db.AttendanceMarks.Add(new AttendanceMark { Id = ++markId, AttendanceRegisterId = register.Id, StudentId = i + 1, MeetingDate = meeting, Present = present });
                }
            }
        }

        db.ChapterEvents.Add(new ChapterEvent
        {
            Id = 5, Date = today.AddDays(28), Title = "Chapter meeting", StartTime = new TimeOnly(14, 0), EndTime = new TimeOnly(16, 0),
            Location = "Church hall", CreatedByUserId = teacher.UserId, CreatedAt = now,
        });
        db.ClassTeachers.Add(new ClassTeacher { Id = 1, RankId = 3, TeacherUserId = teacher.UserId });

        // The liturgical calendar for this month and next (the real feed is imported by Vlms.Jobs).
        var readingId = 0;
        var firstDay = new DateOnly(today.Year, today.Month, 1);
        for (var day = firstDay; day < firstDay.AddMonths(2); day = day.AddDays(1))
        {
            var fast = day.DayOfWeek is DayOfWeek.Wednesday or DayOfWeek.Friday ? LiturgicalFast.WineAndOil : LiturgicalFast.None;
            var liturgical = new LiturgicalDay
            {
                Date = day, Title = day.DayOfWeek == DayOfWeek.Sunday ? "Sunday of the Holy Fathers" : "Martyrs Marcian and Martyrius",
                Fast = fast, Saints = "Martyrs Marcian and Martyrius; Tabitha the Merciful",
            };
            liturgical.Readings.Add(new LiturgicalReading { Id = ++readingId, Date = day, Order = 1, Heading = "Epistle Reading", Reference = "Galatians 2:16-20", Text = "Brethren, knowing that a man is not justified by works of the law but through faith in Jesus Christ." });
            liturgical.Readings.Add(new LiturgicalReading { Id = ++readingId, Date = day, Order = 2, Heading = "Gospel Reading", Reference = "Luke 8:26-39", Text = "At that time, as Jesus arrived at the country of the Gadarenes, there met him a man from the city who had demons." });
            db.LiturgicalDays.Add(liturgical);
        }

        await db.SaveChangesAsync();

        return personas;
    }

    public static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("Not found above the test output folder: " + relative);
    }

    /// <summary>RFC 6238, SHA-1, 6 digits, 30 s step.</summary>
    public static string Totp(string base32Key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in base32Key.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        var hash = HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private sealed class SeedUserContext : ICurrentUserContext
    {
        public int? UserId => 101;
        public bool HasRole(Role role) => role == Role.Admin;
        public bool HasMfaEnabled => true;
        public int? ActiveChapterId => Chapter.ShrewsburyId;
        public bool SeesAllChapters => true;
    }
}
