using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vlms.Domain;
using Vlms.Infrastructure;
using Vlms.Infrastructure.Chapters;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Safeguarding;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Chapter separation (functional.md FR-021, docs/adr/0010-chapters.md; test-plan.md TC-043 to TC-051).
/// Two chapters, each with one record of every chapter-owned entity, built by the real
/// <see cref="VlmsDbContext"/> (its chapter query filters and write rules) on SQLite in memory.
/// The reflection tests fail if a new entity is added without being classified as chapter-owned
/// (and filtered) or national.
/// </summary>
public sealed class ChapterSeparationTests : IDisposable
{
    private const int Coventry = 2;

    // Per chapter c, ids are c*100 + offset (Shrewsbury 1xx, Coventry 2xx).
    private const int Staff = 1;     // Admin, Safeguarding Officer and Teacher in their chapter
    private const int ParentUser = 2;
    private const int StudentOffset = 10;
    private const int GuardianOffset = 20;

    private const int NationalAdmin = 900;   // National Administrator, no chapter role
    private const int TwoChapterUser = 901;  // Teacher in Shrewsbury, Admin in Coventry
    private const int UnlinkedCoventryStudent = 299;

    // A recent meeting, so the national figures (last 12 months) include it.
    private static readonly DateOnly MeetingDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7);

    /// <summary>The entities that are national by design (the curriculum, ranks, badges, roles, chapters, audit).</summary>
    private static readonly HashSet<Type> NationalOrIdentityTypes =
    [
        typeof(Rank), typeof(Lesson), typeof(LessonTagAssignment), typeof(LessonFile), typeof(LessonPicture), typeof(TeachersInformationPicture),
        typeof(LessonVersion), typeof(LessonVersionTag), typeof(LessonVersionPicture), typeof(LessonSuggestion), typeof(LessonSuggestionTag),
        typeof(LessonSuggestionPicture), typeof(LessonPublishingSettingChange),
        typeof(RankBadge), typeof(AppUser), typeof(UserRole), typeof(Chapter), typeof(ActiveChapterChoice),
        typeof(SensitiveDataAccessLog), typeof(VlmsIdentityUser),
        typeof(LiturgicalDay), typeof(LiturgicalReading), typeof(CalendarFeedToken), typeof(CalendarViewChoice), typeof(AddedSaint),
        typeof(IdentityUserClaim<int>), typeof(IdentityUserLogin<int>), typeof(IdentityUserToken<int>)
    ];

    private readonly SqliteConnection _anchor;
    private readonly string _connectionString;

    public ChapterSeparationTests()
    {
        _connectionString = $"Data Source=file:vlms-chapters-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connectionString);
        _anchor.Open();

        using var db = NewContext(TestSeedUserContext.Instance);
        db.Database.EnsureCreated();
        db.Chapters.Add(new Chapter
        {
            Id = Coventry, Name = "The Greek Orthodox Church of the Holy Transfiguration, Coventry", Place = "Coventry",
            CreatedAt = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc), IsActive = true
        });
        db.Ranks.Add(new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" });
        db.Lessons.Add(new Lesson { Id = 1, RankId = 1, Code = "L1", Title = "Knots", Order = 1, IsActive = true });
        db.RankBadges.Add(new RankBadge { Id = 1, RankId = 1, ImageBlobKey = "badges/1.png" });
        db.SaveChanges();

        SeedChapter(db, Chapter.ShrewsburyId);
        SeedChapter(db, Coventry);

        db.Students.Add(NewStudent(UnlinkedCoventryStudent, Coventry));
        AddUser(db, NationalAdmin, (Role.NationalAdministrator, null));
        AddUser(db, TwoChapterUser, (Role.Teacher, Chapter.ShrewsburyId), (Role.Admin, Coventry));
        db.SaveChanges();
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _anchor.Dispose();
    }

    // ------------------------------------------------------------------ fixture

    private readonly List<ServiceProvider> _providers = new();

    // Through DI, as the app does: the read-audit interceptor resolves the context's options from it.
    private VlmsDbContext NewContext(ICurrentUserContext user)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(user);
        services.AddDbContext<VlmsDbContext>(options => options.UseSqlite(_connectionString));
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.CreateScope().ServiceProvider.GetRequiredService<VlmsDbContext>();
    }

    private static void SeedChapter(VlmsDbContext db, int c)
    {
        var b = c * 100;
        AddUser(db, b + Staff, (Role.Admin, c), (Role.SafeguardingOfficer, c), (Role.Teacher, c));
        AddUser(db, b + ParentUser, (Role.Parent, c));

        // Top-level rows name their chapter; every child row is left at 0 and takes its parent's.
        db.Students.Add(NewStudent(b + StudentOffset, c));
        db.ParentGuardians.Add(new ParentGuardian
        {
            Id = b + GuardianOffset, ChapterId = c, Name = $"Parent {c}", ContactInfo = $"parent{c}@example.com",
            IsPrimary = true, AppUserId = b + ParentUser
        });
        db.StudentGuardianLinks.Add(new StudentGuardianLink { StudentId = b + StudentOffset, ParentGuardianId = b + GuardianOffset, CreatedByUserId = b + Staff });
        db.StudentLessonCompletions.Add(new StudentLessonCompletion
        {
            Id = b + 30, StudentId = b + StudentOffset, LessonId = 1, CompletedByUserId = b + Staff,
            CompletedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), IsReversed = false
        });
        db.Certificates.Add(new Certificate { Id = b + 40, StudentLessonCompletionId = b + 30, GeneratedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), BlobKey = $"c/{b}.pdf" });
        db.StudentBadges.Add(new StudentBadge { Id = b + 50, StudentId = b + StudentOffset, RankBadgeId = 1, AwardedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.StudentRankProgresses.Add(new StudentRankProgress { Id = b + 60, StudentId = b + StudentOffset, RankId = 1, StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        db.ConsentRecords.Add(new ConsentRecord
        {
            Id = b + 70, StudentId = b + StudentOffset, PeriodStart = new DateOnly(2025, 9, 1), PeriodEnd = new DateOnly(2026, 8, 31),
            PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
            Status = ConsentStatus.Approved, SubmittedByParentId = b + GuardianOffset, ExpiryDate = new DateOnly(2027, 8, 31)
        });
        db.ConsentSensitiveDetails.Add(new ConsentSensitiveDetails { Id = b + 70, ConsentRecordId = b + 70, EmergencyContact = $"Contact {c}" });
        db.DbsChecks.Add(new DbsCheck
        {
            Id = b + 80, ChapterId = c, TeacherUserId = b + Staff, CheckDate = new DateOnly(2025, 1, 1),
            ExpiryDate = new DateOnly(2028, 1, 1), CertificateNumber = $"DBS{b}", Status = DbsCheckStatus.Clear
        });
        db.GuardianInvites.Add(new GuardianInvite
        {
            Id = b + 90, StudentId = b + StudentOffset, ParentGuardianId = b + GuardianOffset, CreatedByUserId = b + Staff,
            CreatedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), ExpiresAt = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero)
        });
        db.StudentApplications.Add(new StudentApplication
        {
            Id = b + 95, ChapterId = c, ApplicantUserId = b + ParentUser, ParentPhone = "07000 000000", ParentPostalAddress = "1 Church Lane",
            Relationship = "Mother", ChildName = $"Applicant child {c}", ChildDateOfBirth = new DateOnly(2016, 1, 1),
            PhotoMediaConsent = true, TransportConsent = true, OffsiteTripConsent = true, DataSharingConsent = true,
            Status = StudentApplicationStatus.AwaitingSafeguardingReview,
            SubmittedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), LastSubmittedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        db.StudentApplicationSensitiveDetails.Add(new StudentApplicationSensitiveDetails { Id = b + 95, StudentApplicationId = b + 95, EmergencyContact = $"Contact {c}" });
        db.ChapterEvents.Add(new ChapterEvent { Id = b + 96, ChapterId = c, Date = MeetingDate, Title = $"Meeting {c}", CreatedByUserId = b + Staff, CreatedAt = DateTime.UtcNow });
        db.ClassTeachers.Add(new ClassTeacher { Id = b + 98, ChapterId = c, RankId = 1, TeacherUserId = b + Staff });
        db.AttendanceRegisters.Add(new AttendanceRegister { Id = b + 97, ChapterEventId = b + 96, RankId = 1, MeetingDate = MeetingDate, TakenByUserId = b + Staff, TakenAt = DateTime.UtcNow });
        db.AttendanceMarks.Add(new AttendanceMark { Id = b + 97, AttendanceRegisterId = b + 97, StudentId = b + StudentOffset, MeetingDate = MeetingDate, Present = true });
        db.SaveChanges();
    }

    private static Student NewStudent(int id, int chapterId) => new()
    {
        Id = id, ChapterId = chapterId, Name = $"Student {id}", DateOfBirth = new DateOnly(2015, 1, 1), CurrentRankId = 1,
        Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2025, 9, 1)
    };

    private static void AddUser(VlmsDbContext db, int id, params (Role Role, int? Chapter)[] roles)
    {
        var email = $"user{id}@example.com";
        db.Users.Add(new VlmsIdentityUser
        {
            Id = id, UserName = email, NormalizedUserName = email.ToUpperInvariant(), Email = email,
            NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true, TwoFactorEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N")
        });
        db.AppUsers.Add(new AppUser { Id = id, DisplayName = $"User {id}", Email = email });
        foreach (var (role, chapter) in roles)
        {
            db.UserRoles.Add(new UserRole { UserId = id, Role = role, ChapterId = chapter });
        }
    }

    /// <summary>Admin, Safeguarding Officer and Teacher with MFA, working in <paramref name="chapterId"/>: sees the most.</summary>
    private static FakeCurrentUserContext StaffOf(int chapterId) =>
        new(chapterId * 100 + Staff, Role.Admin, Role.SafeguardingOfficer, Role.Teacher) { ActiveChapterId = chapterId };

    private static IEnumerable<Type> ChapterOwnedTypes(VlmsDbContext db) =>
        db.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => typeof(IChapterOwned).IsAssignableFrom(t)).OrderBy(t => t.Name);

    public static TheoryData<string> ChapterOwnedEntityNames()
    {
        var data = new TheoryData<string>();
        foreach (var type in typeof(IChapterOwned).Assembly.GetTypes()
                     .Where(t => t.IsClass && typeof(IChapterOwned).IsAssignableFrom(t)).OrderBy(t => t.Name))
        {
            data.Add(type.Name);
        }

        return data;
    }

    /// <summary>The chapter of every row of <paramref name="entityName"/> the caller can read.</summary>
    private static List<int> VisibleChapters(VlmsDbContext db, string entityName)
    {
        var type = typeof(IChapterOwned).Assembly.GetTypes().Single(t => t.Name == entityName);
        var method = typeof(ChapterSeparationTests).GetMethod(nameof(VisibleChaptersOf), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(type);
        return (List<int>)method.Invoke(null, [db])!;
    }

    // Whole entities (not a projection), so the sensitive ones go through the read audit as any read would.
    private static List<int> VisibleChaptersOf<T>(VlmsDbContext db) where T : class, IChapterOwned =>
        db.Set<T>().AsNoTracking().ToList().Select(x => x.ChapterId).ToList();

    // ------------------------------------------------------------------ TC-043: every entity classified and filtered

    [Fact]
    public void EveryChapterOwnedEntity_HasTheChapterQueryFilter_AndAForeignKeyToChapter()
    {
        using var db = NewContext(StaffOf(1));
        var owned = ChapterOwnedTypes(db).ToList();

        Assert.Equal(17, owned.Count);
        foreach (var type in owned)
        {
            var entityType = db.Model.FindEntityType(type)!;
            Assert.True(entityType.FindDeclaredQueryFilter(VlmsDbContext.ChapterFilter) is not null,
                $"{type.Name} is chapter-owned but has no \"{VlmsDbContext.ChapterFilter}\" query filter.");
            Assert.Contains(entityType.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(Chapter)
                && fk.Properties.Single().Name == nameof(IChapterOwned.ChapterId));
        }
    }

    [Fact]
    public void EveryEntityInTheModel_IsEitherChapterOwnedOrListedAsNational()
    {
        using var db = NewContext(StaffOf(1));

        var unclassified = db.Model.GetEntityTypes()
            .Select(t => t.ClrType)
            .Where(t => !typeof(IChapterOwned).IsAssignableFrom(t) && !NationalOrIdentityTypes.Contains(t))
            .Select(t => t.Name)
            .ToList();

        Assert.True(unclassified.Count == 0,
            "Classify each new entity as chapter-owned (IChapterOwned, with the Chapter filter) or national: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void TheSensitiveEntities_KeepTheirSensitiveDataFilter_AlongsideTheChapterFilter()
    {
        using var db = NewContext(StaffOf(1));
        foreach (var type in new[] { typeof(ConsentSensitiveDetails), typeof(DbsCheck), typeof(StudentApplicationSensitiveDetails) })
        {
            var entityType = db.Model.FindEntityType(type)!;
            Assert.NotNull(entityType.FindDeclaredQueryFilter(VlmsDbContext.SensitiveDataFilter));
            Assert.NotNull(entityType.FindDeclaredQueryFilter(VlmsDbContext.ChapterFilter));
        }
    }

    // ------------------------------------------------------------------ TC-044: per entity, chapter A sees none of B

    [Theory]
    [MemberData(nameof(ChapterOwnedEntityNames))]
    public void StaffOfShrewsbury_SeeOnlyShrewsburyRows(string entityName)
    {
        using var db = NewContext(StaffOf(Chapter.ShrewsburyId));
        var chapters = VisibleChapters(db, entityName);

        Assert.NotEmpty(chapters);
        Assert.All(chapters, c => Assert.Equal(Chapter.ShrewsburyId, c));
    }

    [Theory]
    [MemberData(nameof(ChapterOwnedEntityNames))]
    public void StaffOfCoventry_SeeOnlyCoventryRows(string entityName)
    {
        using var db = NewContext(StaffOf(Coventry));
        var chapters = VisibleChapters(db, entityName);

        Assert.NotEmpty(chapters);
        Assert.All(chapters, c => Assert.Equal(Coventry, c));
    }

    [Theory]
    [MemberData(nameof(ChapterOwnedEntityNames))]
    public void NationalAdministrator_WithNoChapterRole_SeesNoChapterRows(string entityName)
    {
        using var db = NewContext(new FakeCurrentUserContext(NationalAdmin, Role.NationalAdministrator) { ActiveChapterId = null });

        Assert.Empty(VisibleChapters(db, entityName));
    }

    // ------------------------------------------------------------------ TC-045: parents keep their own children

    [Fact]
    public void Parent_SeesTheirOwnChildInAnotherChapter_ButNoOtherChildThere()
    {
        // The Coventry parent working in Shrewsbury (say they hold Parent in both): their own Coventry
        // child and its records, never Coventry's unlinked student.
        using var db = NewContext(new FakeCurrentUserContext(Coventry * 100 + ParentUser, Role.Parent) { ActiveChapterId = Chapter.ShrewsburyId });

        var students = db.Students.Select(s => s.Id).ToList();
        Assert.Contains(Coventry * 100 + StudentOffset, students);
        Assert.DoesNotContain(UnlinkedCoventryStudent, students);
        Assert.Contains(Coventry * 100 + 70, db.ConsentRecords.Select(c => c.Id).ToList());
        Assert.Contains(Coventry * 100 + 70, db.ConsentSensitiveDetails.Select(d => d.Id).ToList());
        Assert.Contains(Coventry * 100 + 40, db.Certificates.Select(c => c.Id).ToList());
        Assert.Contains(Coventry * 100 + 97, db.AttendanceMarks.Select(m => m.Id).ToList());
    }

    [Fact]
    public void StaffWhoAreParentsElsewhere_DoNotFindTheirOwnChildInTheirStaffChapter()
    {
        // The Coventry parent as a Teacher in Shrewsbury: staff lists show Shrewsbury only. They see
        // their child by switching to Coventry, where they hold Parent.
        using var db = NewContext(new FakeCurrentUserContext(Coventry * 100 + ParentUser, Role.Teacher) { ActiveChapterId = Chapter.ShrewsburyId });

        Assert.DoesNotContain(db.Students.ToList(), s => s.ChapterId == Coventry);
        Assert.DoesNotContain(db.ConsentRecords.ToList(), c => c.ChapterId == Coventry);
    }

    [Fact]
    public async Task Applicant_SeesTheirOwnApplicationInAnyChapter_AndTheStaffListsDoNot()
    {
        using (var db = NewContext(new FakeCurrentUserContext(Coventry * 100 + ParentUser) { ActiveChapterId = null }))
        {
            Assert.Equal([Coventry * 100 + 95], db.StudentApplications.Select(a => a.Id).ToList());
        }

        // The same person as Safeguarding Officer of Shrewsbury: their Coventry application is not
        // in Shrewsbury's review list.
        using var asOfficer = NewContext(new FakeCurrentUserContext(Coventry * 100 + ParentUser, Role.SafeguardingOfficer) { ActiveChapterId = Chapter.ShrewsburyId });
        var officer = new FakeCurrentUserContext(Coventry * 100 + ParentUser, Role.SafeguardingOfficer) { ActiveChapterId = Chapter.ShrewsburyId };
        var service = new Vlms.Infrastructure.Registration.StudentApplicationService(asOfficer, officer, null!);
        var listed = (await service.ListForSafeguardingAsync()).Select(a => a.Id).ToList();
        Assert.Equal([Chapter.ShrewsburyId * 100 + 95], listed);
    }

    // ------------------------------------------------------------------ TC-046: write rules

    [Fact]
    public void AChildRecord_ForAnotherChaptersStudent_IsRefused()
    {
        using var db = NewContext(StaffOf(Chapter.ShrewsburyId));
        db.StudentLessonCompletions.Add(new StudentLessonCompletion
        {
            Id = 999, StudentId = Coventry * 100 + StudentOffset, LessonId = 1, CompletedByUserId = 101,
            CompletedAt = DateTime.UtcNow, IsReversed = false
        });

        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public void AnAttendanceMark_ForAnotherChaptersStudent_IsRefused_EvenOnTheCallersOwnRegister()
    {
        using var db = NewContext(TestSeedUserContext.Instance);
        db.AttendanceMarks.Add(new AttendanceMark
        {
            Id = 999, AttendanceRegisterId = Chapter.ShrewsburyId * 100 + 97, StudentId = UnlinkedCoventryStudent,
            MeetingDate = MeetingDate, Present = true
        });

        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public void ATopLevelRecord_InAnotherChapter_IsRefused_AndOneLeftAtZero_GoesIntoTheActiveChapter()
    {
        using (var refused = NewContext(StaffOf(Chapter.ShrewsburyId)))
        {
            refused.Students.Add(NewStudent(998, Coventry));
            Assert.Throws<InvalidOperationException>(() => refused.SaveChanges());
        }

        using var db = NewContext(StaffOf(Coventry));
        db.Students.Add(NewStudent(997, chapterId: 0));
        db.SaveChanges();
        Assert.Equal(Coventry, db.Students.AsNoTracking().Single(s => s.Id == 997).ChapterId);
    }

    [Fact]
    public void ALinkBetweenAStudentAndAGuardianOfDifferentChapters_IsRefused()
    {
        using var db = NewContext(TestSeedUserContext.Instance);
        db.StudentGuardianLinks.Add(new StudentGuardianLink
        {
            StudentId = Chapter.ShrewsburyId * 100 + StudentOffset, ParentGuardianId = Coventry * 100 + GuardianOffset, CreatedByUserId = 101
        });

        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    [Fact]
    public void ARecordsChapter_CannotBeChanged_ButACopyLeavingItAtZeroKeepsIt()
    {
        using (var db = NewContext(StaffOf(Chapter.ShrewsburyId)))
        {
            var student = db.Students.Single(s => s.Id == 110);
            db.Entry(student).Property(nameof(Student.ChapterId)).CurrentValue = Coventry;
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }

        using (var db = NewContext(StaffOf(Chapter.ShrewsburyId)))
        {
            var student = db.Students.Single(s => s.Id == 110);
            db.Entry(student).CurrentValues.SetValues(new Student
            {
                Id = 110, Name = "Renamed", DateOfBirth = student.DateOfBirth, CurrentRankId = 1,
                Status = student.Status, EnrolmentDate = student.EnrolmentDate
            });
            db.SaveChanges();
        }

        using var verify = NewContext(TestSeedUserContext.Instance);
        var saved = verify.Students.Single(s => s.Id == 110);
        Assert.Equal(("Renamed", Chapter.ShrewsburyId), (saved.Name, saved.ChapterId));
    }

    [Fact]
    public async Task NextIds_AreUniqueAcrossChapters_ThoughEachChapterSeesOnlyItsOwnRows()
    {
        using var db = NewContext(StaffOf(Chapter.ShrewsburyId));

        Assert.Equal(UnlinkedCoventryStudent + 1, await db.NextIdAsync<Student>());
        Assert.True(db.Students.Max(s => s.Id) < UnlinkedCoventryStudent);
    }

    // ------------------------------------------------------------------ TC-047: roles per chapter, active chapter

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }

    private IdentityCurrentUserContext RealContextFor(int userId) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString(System.Globalization.CultureInfo.InvariantCulture))], "test")),
        new DbContextOptionsBuilder<VlmsDbContext>().UseSqlite(_connectionString).Options);

    private void ChooseChapter(int userId, int chapterId)
    {
        using var db = NewContext(TestSeedUserContext.Instance);
        db.ActiveChapterChoices.Add(new ActiveChapterChoice { UserId = userId, ChapterId = chapterId });
        db.SaveChanges();
    }

    [Fact]
    public void ATeacherInOneChapterWhoIsAdminInAnother_HasNoAdminPowersInTheFirst()
    {
        var current = RealContextFor(TwoChapterUser);

        // No choice yet: the lowest-numbered chapter they belong to, Shrewsbury.
        Assert.Equal(Chapter.ShrewsburyId, current.ActiveChapterId);
        Assert.True(current.HasRole(Role.Teacher));
        Assert.False(current.HasRole(Role.Admin));

        ChooseChapter(TwoChapterUser, Coventry);

        Assert.Equal(Coventry, current.ActiveChapterId);
        Assert.True(current.HasRole(Role.Admin));
        Assert.False(current.HasRole(Role.Teacher));
    }

    [Fact]
    public void AChoiceOfAChapterWithoutARole_IsIgnored_AndNationalRolesNeedNoChapter()
    {
        ChooseChapter(NationalAdmin, Coventry);

        var national = RealContextFor(NationalAdmin);
        Assert.Null(national.ActiveChapterId);
        Assert.True(national.HasRole(Role.NationalAdministrator));
        Assert.False(national.HasRole(Role.Admin));

        using var db = NewContext(national);
        Assert.Empty(db.Students.ToList());
        Assert.Empty(db.DbsChecks.ToList());
    }

    [Fact]
    public async Task SwitchingToAChapterWithoutARole_IsRefused()
    {
        var current = RealContextFor(TwoChapterUser);
        using var provider = TestIdentityServices.BuildServiceProvider(_connectionString, current);
        using var scope = provider.CreateScope();
        var service = new ChapterService(scope.ServiceProvider.GetRequiredService<VlmsDbContext>(), current,
            scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>());

        await service.SwitchToAsync(Coventry);
        Assert.Equal(Coventry, current.ActiveChapterId);

        using (var db = NewContext(TestSeedUserContext.Instance))
        {
            db.Chapters.Add(new Chapter { Id = 3, Name = "Third", Place = "Elsewhere", CreatedAt = DateTime.UtcNow, IsActive = true });
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SwitchToAsync(3));
        Assert.Equal(Coventry, current.ActiveChapterId);
    }

    // ------------------------------------------------------------------ TC-048: chapter Admins manage their own chapter only

    private RoleAssignmentService RolesAs(int chapterId, out ServiceProvider provider)
    {
        var admin = StaffOf(chapterId);
        provider = TestIdentityServices.BuildServiceProvider(_connectionString, admin);
        var scope = provider.CreateScope();
        return new RoleAssignmentService(scope.ServiceProvider.GetRequiredService<VlmsDbContext>(), admin,
            scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>());
    }

    [Fact]
    public async Task ChapterAdmin_ListsAndChangesOnlyTheirOwnChaptersRoles()
    {
        var coventryAdmin = RolesAs(Coventry, out var provider);
        using var _ = provider;

        var listed = (await coventryAdmin.ListUsersAsync()).Select(u => u.UserId).ToList();
        Assert.Contains(Coventry * 100 + Staff, listed);
        Assert.DoesNotContain(Chapter.ShrewsburyId * 100 + Staff, listed);
        // The two-chapter user appears with their Coventry role only.
        Assert.Equal([Role.Admin], (await coventryAdmin.ListUsersAsync()).Single(u => u.UserId == TwoChapterUser).Roles);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coventryAdmin.RevokeAsync(TwoChapterUser, Role.Teacher));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coventryAdmin.GrantAsync(Chapter.ShrewsburyId * 100 + Staff, Role.Approver));

        await coventryAdmin.GrantAsync(Chapter.ShrewsburyId * 100 + Staff, Role.Teacher);
        using var verify = NewContext(TestSeedUserContext.Instance);
        Assert.Equal(Coventry, verify.UserRoles.Single(r => r.UserId == Chapter.ShrewsburyId * 100 + Staff && r.Role == Role.Teacher && r.ChapterId != Chapter.ShrewsburyId).ChapterId);
    }

    [Fact]
    public async Task TheLastAdminGuard_CountsOnlyTheChaptersOwnAdmins()
    {
        // Shrewsbury has one Admin (101). The Coventry Admins do not count for Shrewsbury.
        var shrewsburyAdmin = RolesAs(Chapter.ShrewsburyId, out var provider);
        using var _ = provider;

        await Assert.ThrowsAsync<InvalidOperationException>(() => shrewsburyAdmin.RevokeAsync(Chapter.ShrewsburyId * 100 + Staff, Role.Admin));
    }

    // ------------------------------------------------------------------ TC-049: national administrator, least privilege

    private ChapterService ChaptersAs(ICurrentUserContext user, out ServiceProvider provider)
    {
        provider = TestIdentityServices.BuildServiceProvider(_connectionString, user);
        var scope = provider.CreateScope();
        return new ChapterService(scope.ServiceProvider.GetRequiredService<VlmsDbContext>(), user,
            scope.ServiceProvider.GetRequiredService<UserManager<VlmsIdentityUser>>());
    }

    [Fact]
    public async Task NationalAdministrator_CreatesChapters_AndAppointsAndRemovesTheirAdmins()
    {
        var national = new FakeCurrentUserContext(NationalAdmin, Role.NationalAdministrator) { ActiveChapterId = null };
        var service = ChaptersAs(national, out var provider);
        using var _ = provider;

        var created = await service.CreateAsync("Holy Trinity", "Northtown");
        await service.AppointAdminAsync(created.Id, $"user{Chapter.ShrewsburyId * 100 + ParentUser}@example.com");
        Assert.Equal([Chapter.ShrewsburyId * 100 + ParentUser], (await service.ListAdminsAsync(created.Id)).Select(a => a.UserId));

        // The last Admin of a chapter stays until another is appointed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveAdminAsync(created.Id, Chapter.ShrewsburyId * 100 + ParentUser));
        await service.AppointAdminAsync(created.Id, $"user{Coventry * 100 + ParentUser}@example.com");
        await service.RemoveAdminAsync(created.Id, Chapter.ShrewsburyId * 100 + ParentUser);
        Assert.Equal([Coventry * 100 + ParentUser], (await service.ListAdminsAsync(created.Id)).Select(a => a.UserId));
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.SafeguardingOfficer)]
    [InlineData(Role.Approver)]
    public async Task OnlyANationalAdministrator_ManagesChapters(Role role)
    {
        var other = new FakeCurrentUserContext(Chapter.ShrewsburyId * 100 + Staff, role);
        var service = ChaptersAs(other, out var provider);
        using var _ = provider;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync("X", "Y"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AppointAdminAsync(Coventry, "user101@example.com"));
    }

    [Fact]
    public async Task NationalAdministrator_WithoutMfa_IsRefused()
    {
        var noMfa = new FakeCurrentUserContext(NationalAdmin, hasMfaEnabled: false, Role.NationalAdministrator) { ActiveChapterId = null };
        var service = ChaptersAs(noMfa, out var provider);
        using var _ = provider;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAllAsync());
    }

    [Fact]
    public async Task NationalTotals_CountStudentsPerChapterAndRank_WithoutNames()
    {
        var national = new FakeCurrentUserContext(NationalAdmin, Role.NationalAdministrator) { ActiveChapterId = null };
        using var db = NewContext(national);
        var totals = await new NationalTotalsService(db, national).GetAsync();

        Assert.Equal(1, totals.Single(t => t.ChapterId == Chapter.ShrewsburyId).StudentsOnRoll);
        Assert.Equal(2, totals.Single(t => t.ChapterId == Coventry).StudentsOnRoll);
        Assert.Equal(2, totals.Single(t => t.ChapterId == Coventry).ByRank.Single(r => r.RankId == 1).Students);
        var month = Assert.Single(totals.Single(t => t.ChapterId == Coventry).Attendance);
        Assert.Equal((MeetingDate.Year, MeetingDate.Month, 1, 1, 1, (int?)100), (month.Year, month.Month, month.Meetings, month.Marks, month.Present, month.RatePercent));

        var admin = StaffOf(Chapter.ShrewsburyId);
        using var adminDb = NewContext(admin);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new NationalTotalsService(adminDb, admin).GetAsync());
    }

    [Fact]
    public async Task NationalAdministratorBootstrap_GrantsTheRoleOnce_ThenIsInert()
    {
        using (var db = NewContext(TestSeedUserContext.Instance))
        {
            db.UserRoles.RemoveRange(db.UserRoles.Where(r => r.Role == Role.NationalAdministrator));
            db.SaveChanges();
        }

        using (var db = NewContext(TestSeedUserContext.Instance))
        {
            var user = await new FirstAdminBootstrapService(db).BootstrapNationalAdministratorAsync("USER101@example.com");
            Assert.Equal(101, user.Id);
        }

        using var again = NewContext(TestSeedUserContext.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FirstAdminBootstrapService(again).BootstrapNationalAdministratorAsync("user102@example.com"));
        Assert.Equal([(101, (int?)null)], again.UserRoles.Where(r => r.Role == Role.NationalAdministrator).Select(r => new { r.UserId, r.ChapterId }).AsEnumerable().Select(r => (r.UserId, r.ChapterId)).ToList());
    }

    // ------------------------------------------------------------------ TC-050: notifications go to the chapter's own staff

    [Fact]
    public async Task ConsentSubmittedAlert_GoesOnlyToTheStudentsChaptersStaff()
    {
        var notifications = new FakeNotificationService();
        using var db = NewContext(new FakeCurrentUserContext(Coventry * 100 + ParentUser, Role.Parent) { ActiveChapterId = Coventry });
        var service = new ConsentNotificationService(db, notifications, NullLogger<ConsentNotificationService>.Instance, null);

        await service.NotifySubmittedAsync(Coventry * 100 + 70);

        var recipients = notifications.SentRequests.Select(r => r.RecipientEmail).ToList();
        Assert.Contains($"user{Coventry * 100 + Staff}@example.com", recipients);
        Assert.Contains($"user{TwoChapterUser}@example.com", recipients); // Admin of Coventry
        Assert.DoesNotContain($"user{Chapter.ShrewsburyId * 100 + Staff}@example.com", recipients);
    }

    [Fact]
    public async Task SafeguardingEscalation_IsOneDigestPerChapter_ToThatChaptersStaff()
    {
        var notifications = new FakeNotificationService();
        using var db = NewContext(SystemCurrentUserContext.Instance);
        var result = new ConsentExpiryJobResult(
            [new ConsentExpiryFlag(110, "Student 110", null, null, IsExpired: true, Chapter.ShrewsburyId),
             new ConsentExpiryFlag(210, "Student 210", null, null, IsExpired: true, Coventry)],
            [], [], [], []);

        await new ConsentExpiryNotifier(db, SystemCurrentUserContext.Instance, notifications).NotifyAsync(result);

        var toShrewsbury = notifications.SentRequests.Single(r => r.RecipientEmail == $"user{Chapter.ShrewsburyId * 100 + Staff}@example.com");
        Assert.Contains("Student 110", toShrewsbury.Body);
        Assert.DoesNotContain("Student 210", toShrewsbury.Body);
        var toCoventry = notifications.SentRequests.Single(r => r.RecipientEmail == $"user{Coventry * 100 + Staff}@example.com");
        Assert.DoesNotContain("Student 110", toCoventry.Body);
    }
}
