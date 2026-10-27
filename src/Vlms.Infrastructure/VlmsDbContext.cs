using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Vlms.Domain;
using Vlms.Infrastructure.Auditing;
using Vlms.Infrastructure.Chapters;
using Vlms.Infrastructure.Identity;
using Vlms.Infrastructure.Naming;

namespace Vlms.Infrastructure;

/// <summary>
/// EF Core context for VLMS (self-hosted PostgreSQL,
/// adr/0005-hosting-pivot-self-hosted-vps-postgresql.md — supersedes the original Azure SQL
/// Database row of adr/0001-technology-stack.md; the access-control/audit design below is
/// provider-independent and did not change). Applies the whole-entity sensitive-data restriction
/// and read-audit logging from adr/0004-sensitive-data-access-control.md, and PostgreSQL's own
/// snake_case identifier convention to every table/column name (<see cref="SnakeCaseNamingConvention"/>).
///
/// **Base class, STATE.md "Identity" item:** <see cref="IdentityUserContext{TUser,TKey}"/> (not
/// <see cref="IdentityDbContext{TUser,TRole,TKey}"/>) — the "no role store" variant, deliberately.
/// ASP.NET Core Identity's own role feature (<c>AspNetRoles</c>/<c>AspNetUserRoles</c>) is never
/// used here: roles stay the application-owned <see cref="UserRole"/> table
/// (adr/0002-roles-as-application-claims.md, explicitly unaffected by the identity-provider swap),
/// wired up via <c>AddIdentityCore&lt;VlmsIdentityUser&gt;()</c> (not <c>AddIdentity</c>, which would
/// also register a <c>RoleManager</c> this codebase has no use for) in Vlms.Web's Program.cs.
/// <see cref="OnModelCreating"/> calls <c>base.OnModelCreating</c> first, reusing Microsoft's own
/// tested model configuration for the four Identity store tables (<see cref="VlmsIdentityUser"/>
/// itself, plus its claims/logins/tokens) rather than hand-configuring them.
/// </summary>
public sealed class VlmsDbContext : IdentityUserContext<VlmsIdentityUser, int>
{
    private readonly ICurrentUserContext _currentUser;

    public VlmsDbContext(DbContextOptions<VlmsDbContext> options, ICurrentUserContext currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<Rank> Ranks => Set<Rank>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<LessonTagAssignment> LessonTagAssignments => Set<LessonTagAssignment>();
    public DbSet<LessonFile> LessonFiles => Set<LessonFile>();
    public DbSet<LessonPicture> LessonPictures => Set<LessonPicture>();
    public DbSet<TeachersInformationPicture> TeachersInformationPictures => Set<TeachersInformationPicture>();
    public DbSet<LessonVersion> LessonVersions => Set<LessonVersion>();
    public DbSet<LessonVersionTag> LessonVersionTags => Set<LessonVersionTag>();
    public DbSet<LessonVersionPicture> LessonVersionPictures => Set<LessonVersionPicture>();
    public DbSet<LessonSuggestion> LessonSuggestions => Set<LessonSuggestion>();
    public DbSet<LessonSuggestionTag> LessonSuggestionTags => Set<LessonSuggestionTag>();
    public DbSet<LessonSuggestionPicture> LessonSuggestionPictures => Set<LessonSuggestionPicture>();
    public DbSet<LessonSuggestionApproval> LessonSuggestionApprovals => Set<LessonSuggestionApproval>();
    public DbSet<LessonPublishingSettingChange> LessonPublishingSettingChanges => Set<LessonPublishingSettingChange>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<ParentGuardian> ParentGuardians => Set<ParentGuardian>();
    public DbSet<StudentGuardianLink> StudentGuardianLinks => Set<StudentGuardianLink>();
    public DbSet<StudentLessonCompletion> StudentLessonCompletions => Set<StudentLessonCompletion>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<RankBadge> RankBadges => Set<RankBadge>();
    public DbSet<StudentBadge> StudentBadges => Set<StudentBadge>();
    public DbSet<StudentRankProgress> StudentRankProgresses => Set<StudentRankProgress>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<ConsentSensitiveDetails> ConsentSensitiveDetails => Set<ConsentSensitiveDetails>();
    public DbSet<DbsCheck> DbsChecks => Set<DbsCheck>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<GuardianInvite> GuardianInvites => Set<GuardianInvite>();
    public DbSet<StudentApplication> StudentApplications => Set<StudentApplication>();
    public DbSet<StudentApplicationSensitiveDetails> StudentApplicationSensitiveDetails => Set<StudentApplicationSensitiveDetails>();
    public DbSet<SensitiveDataAccessLog> SensitiveDataAccessLogs => Set<SensitiveDataAccessLog>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<ActiveChapterChoice> ActiveChapterChoices => Set<ActiveChapterChoice>();
    public DbSet<AttendanceRegister> AttendanceRegisters => Set<AttendanceRegister>();
    public DbSet<AttendanceMark> AttendanceMarks => Set<AttendanceMark>();
    public DbSet<ChapterEvent> ChapterEvents => Set<ChapterEvent>();
    public DbSet<ClassTeacher> ClassTeachers => Set<ClassTeacher>();
    public DbSet<LiturgicalDay> LiturgicalDays => Set<LiturgicalDay>();
    public DbSet<LiturgicalReading> LiturgicalReadings => Set<LiturgicalReading>();
    public DbSet<CalendarFeedToken> CalendarFeedTokens => Set<CalendarFeedToken>();
    public DbSet<CalendarViewChoice> CalendarViewChoices => Set<CalendarViewChoice>();
    public DbSet<AddedSaint> AddedSaints => Set<AddedSaint>();

    /// <summary>The name of the query filter every <see cref="IChapterOwned"/> entity carries (docs/adr/0010-chapters.md).</summary>
    public const string ChapterFilter = "Chapter";

    /// <summary>The name of the adr/0004 whole-entity restriction on the three sensitive entities.</summary>
    public const string SensitiveDataFilter = "SensitiveData";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(SensitiveDataAuditInterceptor.Instance, UtcDateTimeGuardInterceptor.Instance);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ChapterWriteRules.Apply(this, _currentUser);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ChapterWriteRules.Apply(this, _currentUser);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// The next application-assigned id for <typeparamref name="T"/>: one more than the largest id
    /// in the whole table. Read with a fixed aggregate SQL statement rather than
    /// <c>Set&lt;T&gt;().Max(x =&gt; x.Id)</c>, because that MAX runs through the chapter query filter
    /// (docs/adr/0010-chapters.md) and would hand out an id another chapter already uses. It returns
    /// one number and nothing about any row; the table and column names come from the model, never
    /// from input. Same race as the <c>MAX(id)+1</c> it replaces (no worse): two concurrent inserts
    /// collide on the primary key and the second fails.
    /// </summary>
    public async Task<int> NextIdAsync<T>(CancellationToken ct = default) where T : class
    {
        var entityType = Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not part of the model.");
        var table = entityType.GetTableName()!;
        var column = entityType.FindProperty("Id")!.GetColumnName(StoreObjectIdentifier.Table(table, entityType.GetSchema()))!;
        // Identifiers from the model, not input: nothing here can carry a caller's text.
        var sql = "SELECT COALESCE(MAX(" + column + "), 0) AS \"Value\" FROM " + table;
        var max = await Database.SqlQueryRaw<int>(sql).SingleAsync(ct);
        return max + 1;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Identity's own store tables (VlmsIdentityUser + its claims/logins/tokens) — see this
        // class's doc comment for why IdentityUserContext (no role store) rather than IdentityDbContext.
        base.OnModelCreating(modelBuilder);

        // Every Id/composite key below is application-assigned (ValueGeneratedNever) rather than
        // database identity — deliberately simple for a solo-maintained, tens-of-users system, and
        // it keeps the read-audit tests below fully deterministic. AppUser is the one entity below
        // that is ValueGeneratedNever for a different reason now — see its block and doc comment.

        modelBuilder.Entity<Rank>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasMany(x => x.Lessons).WithOne(x => x.Rank).HasForeignKey(x => x.RankId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.RankBadges).WithOne(x => x.Rank).HasForeignKey(x => x.RankId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.TeachersInformationPictures).WithOne(x => x.Rank).HasForeignKey(x => x.RankId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Lesson>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasMany(x => x.Versions).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Suggestions).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Completions).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Tags).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Files).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Pictures).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ContentVersion).HasMaxLength(64);
            e.HasIndex(x => new { x.RankId, x.Order });
        });

        // App-hosted lesson pictures (adr/0012): unique per lesson/rank by the name the text uses.
        modelBuilder.Entity<LessonPicture>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.LessonId, x.FileName }).IsUnique();
        });

        modelBuilder.Entity<TeachersInformationPicture>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.RankId, x.FileName }).IsUnique();
        });

        modelBuilder.Entity<LessonTagAssignment>(e =>
        {
            e.HasKey(x => new { x.LessonId, x.Tag });
        });

        modelBuilder.Entity<LessonFile>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        // Lesson versions and suggestions (FR-007, docs/adr/0013). Versions are kept permanently; one open (draft or
        // submitted) suggestion per lesson, also enforced by LessonSuggestionService. The filter is plain SQL that
        // PostgreSQL and SQLite both accept, like the other filtered indexes here.
        modelBuilder.Entity<LessonVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ContentVersion).HasMaxLength(64);
            e.HasIndex(x => new { x.LessonId, x.Number }).IsUnique();
            e.HasOne(x => x.Suggestion).WithMany().HasForeignKey(x => x.SuggestionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.SuggestedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.PublishedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.LessonVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Pictures).WithOne(x => x.LessonVersion).HasForeignKey(x => x.LessonVersionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LessonVersionTag>(e => e.HasKey(x => new { x.LessonVersionId, x.Tag }));

        modelBuilder.Entity<LessonVersionPicture>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.LessonVersionId, x.FileName }).IsUnique();
        });

        modelBuilder.Entity<LessonSuggestion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.AuthorUser).WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.DecidedByUser).WithMany().HasForeignKey(x => x.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Rank>().WithMany().HasForeignKey(x => x.RankId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LessonSuggestion>().WithMany().HasForeignKey(x => x.RevisesSuggestionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ReferredByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.CommitteeApprovals).WithOne(x => x.LessonSuggestion).HasForeignKey(x => x.LessonSuggestionId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(x => x.LessonSuggestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Pictures).WithOne(x => x.LessonSuggestion).HasForeignKey(x => x.LessonSuggestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.LessonId).IsUnique().HasFilter("status IN (0, 1)").HasDatabaseName("IX_lesson_suggestions_lesson_id_open");
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<LessonSuggestionTag>(e => e.HasKey(x => new { x.LessonSuggestionId, x.Tag }));

        modelBuilder.Entity<LessonSuggestionPicture>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.LessonSuggestionId, x.FileName }).IsUnique();
        });

        // Committee approvals of a referred suggestion (docs/adr/0013 decision 8): once per Approver, and one row per place
        // (1, 2, 3), so concurrent approvals cannot both count as the same one.
        modelBuilder.Entity<LessonSuggestionApproval>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.ApproverUser).WithMany().HasForeignKey(x => x.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.LessonSuggestionId, x.ApproverUserId }).IsUnique();
            e.HasIndex(x => new { x.LessonSuggestionId, x.Position }).IsUnique();
        });

        modelBuilder.Entity<LessonPublishingSettingChange>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.ChangedByUser).WithMany().HasForeignKey(x => x.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Student>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.CurrentRank).WithMany().HasForeignKey(x => x.CurrentRankId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AssignedTeacherUser).WithMany().HasForeignKey(x => x.AssignedTeacherUserId).OnDelete(DeleteBehavior.Restrict);
            // Self-login link (added during implementation to support self/parent login — see
            // data-design.md and STATE.md).
            e.HasOne(x => x.AppUser).WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Completions).WithOne(x => x.Student).HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.RankProgressHistory).WithOne(x => x.Student).HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Badges).WithOne(x => x.Student).HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.ConsentRecords).WithOne(x => x.Student).HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ParentGuardian>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            // Parent-login link (added during implementation to support self/parent login — see
            // data-design.md and STATE.md).
            e.HasOne(x => x.AppUser).WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StudentGuardianLink>(e =>
        {
            e.HasKey(x => new { x.StudentId, x.ParentGuardianId });
            e.HasOne(x => x.Student).WithMany(x => x.GuardianLinks).HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ParentGuardian).WithMany(x => x.StudentLinks).HasForeignKey(x => x.ParentGuardianId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StudentLessonCompletion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.CompletedByUser).WithMany().HasForeignKey(x => x.CompletedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Certificate).WithOne(x => x.StudentLessonCompletion)
                .HasForeignKey<Certificate>(x => x.StudentLessonCompletionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Certificate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.StudentLessonCompletionId).IsUnique();
        });

        modelBuilder.Entity<RankBadge>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.RankId).IsUnique();
        });

        modelBuilder.Entity<StudentBadge>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.RankBadge).WithMany(x => x.StudentBadges).HasForeignKey(x => x.RankBadgeId).OnDelete(DeleteBehavior.Restrict);
        });

        // Calendar (FR-022): one event per chapter per date (a day the chapter meets).
        modelBuilder.Entity<ChapterEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Location).HasMaxLength(200);
            // At most one Varangians event (class or excursion) a day; parish events are not limited.
            e.HasIndex(x => new { x.ChapterId, x.Date }).IsUnique().HasFilter("kind IN (1, 2)");
            e.HasIndex(x => x.SeriesId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Registers).WithOne(x => x.ChapterEvent).HasForeignKey(x => x.ChapterEventId).OnDelete(DeleteBehavior.Restrict);
        });

        // Class teachers (FR-022): one row per teacher per class (rank) in a chapter.
        modelBuilder.Entity<ClassTeacher>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.ChapterId, x.RankId, x.TeacherUserId }).IsUnique();
            e.HasOne(x => x.Rank).WithMany().HasForeignKey(x => x.RankId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TeacherUser).WithMany().HasForeignKey(x => x.TeacherUserId).OnDelete(DeleteBehavior.Cascade);
        });

        // The liturgical calendar (FR-022): national, one row per day, written by the scheduled import.
        modelBuilder.Entity<LiturgicalDay>(e =>
        {
            e.HasKey(x => x.Date);
            e.HasMany(x => x.Readings).WithOne().HasForeignKey(x => x.Date).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LiturgicalReading>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.Date, x.Order }).IsUnique();
        });

        // Added British and Irish saints (FR-022): national, kept each year on a month and day; a name
        // once per date.
        modelBuilder.Entity<AddedSaint>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.Month, x.Day, x.Name }).IsUnique();
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        // Subscribe links (FR-022): one per person per chapter, found by the hash of its secret.
        modelBuilder.Entity<CalendarFeedToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.ChapterId }).IsUnique();
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Chapter>().WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });

        // The calendar view each person last chose (FR-022): one row per person, removed with them.
        modelBuilder.Entity<CalendarViewChoice>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // Attendance (FR-020, FR-022): one register per event per class (rank), one mark per student on it,
        // and one mark per student per meeting day across the day's registers.
        modelBuilder.Entity<AttendanceRegister>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.ChapterEventId, x.RankId }).IsUnique();
            // An excursion's one register for everyone (no rank): one per event.
            e.HasIndex(x => x.ChapterEventId).IsUnique().HasFilter("rank_id IS NULL").HasDatabaseName("IX_attendance_registers_chapter_event_id_everyone");
            e.HasOne(x => x.Rank).WithMany().HasForeignKey(x => x.RankId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TakenByUser).WithMany().HasForeignKey(x => x.TakenByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Marks).WithOne(x => x.AttendanceRegister).HasForeignKey(x => x.AttendanceRegisterId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AttendanceMark>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.AttendanceRegisterId, x.StudentId }).IsUnique();
            e.HasIndex(x => new { x.StudentId, x.MeetingDate }).IsUnique();
            // Deleted with the student: attendance is part of the student's record (retention).
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StudentRankProgress>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.Rank).WithMany().HasForeignKey(x => x.RankId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConsentRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.SubmittedByParent).WithMany(x => x.SubmittedConsentRecords)
                .HasForeignKey(x => x.SubmittedByParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ApprovedByUser).WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SubmittedByUser).WithMany().HasForeignKey(x => x.SubmittedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SensitiveDetails).WithOne(x => x.ConsentRecord)
                .HasForeignKey<ConsentSensitiveDetails>(x => x.ConsentRecordId).OnDelete(DeleteBehavior.Cascade);
        });

        // ConsentSensitiveDetails (amended by adr/0006-parent-consent-and-teacher-read-access.md,
        // functional.md FR-015, Philip 22/10/2026): a row is visible to
        //   1. Admin/SafeguardingOfficer with MFA enabled (unchanged from adr/0004 pass 7, below);
        //   2. any Teacher with MFA enabled (read; Teachers see all students,
        //      Student.AssignedTeacherUserId is informational only). The MFA conjunct was added on
        //      22/10/2026 (Philip; adr/0006 amendment), matching MfaRequirementHandler, which now
        //      also gates Teacher; and
        //   3. a Parent whose own AppUser is the ParentGuardian.AppUserId on a StudentGuardianLink to
        //      the row's student, and who has not left — their own linked children only.
        // The UserId != null conjunct guards a Parent-role caller with no resolved UserId: EF Core's
        // C# null semantics would otherwise turn AppUserId == UserId into "AppUserId IS NULL" and
        // match every guardian not yet linked to an account. The !HasLeft conjunct also excludes
        // that case on its own (no joined AppUser, so it is not true); the tests showed each alone is
        // enough, and both are kept so neither is a single point of failure. The Approver role
        // matches no arm. Parents are deliberately NOT MFA-gated (MFA is offered to them, never
        // required; not requested by Philip). Writes are not governed by a query filter at
        // all: Teacher has no write path, and a Parent's write is checked against the same link in
        // ParentConsentService. DbsCheck below is deliberately unchanged.
        //
        // Whole-entity sensitive-data restriction (adr/0004 §"MFA-gated roles", pass 7 — STATE.md
        // "Identity" item): only Admin/SafeguardingOfficer see DbsCheck rows (and, for
        // ConsentSensitiveDetails, arm 1 above, plus Teacher in arm 2), AND ONLY once they have TOTP MFA enabled (ICurrentUserContext.HasMfaEnabled). This is a genuine strengthening added
        // alongside the identity-provider swap, not a mechanical rename — MFA enforcement for these
        // two roles (item 4 of that STATE.md item) is only meaningful if it also protects the exact
        // entities ADR-0004 exists to protect, not just the Razor pages that display them; without
        // this, an Admin who never enrolled MFA could still be denied the /safeguarding/* pages by
        // MfaRequirementHandler yet still read DbsCheck/ConsentSensitiveDetails through any other
        // code path that queries VlmsDbContext directly. See openwiki/access-control.md's MFA
        // section for the full reasoning and openwiki/authentication-authorization.md for the
        // enforced-for-Admin/SafeguardingOfficer/Teacher, offered-to-everyone-else scope decision.
        modelBuilder.Entity<ConsentSensitiveDetails>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.ConsentRecordId).IsUnique();
            e.HasQueryFilter(SensitiveDataFilter, x =>
                ((_currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer))
                    && _currentUser.HasMfaEnabled)
                || (_currentUser.HasRole(Role.Teacher) && _currentUser.HasMfaEnabled)
                || (_currentUser.HasRole(Role.Parent)
                    && _currentUser.UserId != null
                    && StudentGuardianLinks.Any(link =>
                        link.StudentId == x.ConsentRecord!.StudentId
                        && link.ParentGuardian!.AppUserId == _currentUser.UserId
                        // A staff leaver who is also a parent keeps parent access (FR-017, LeftStaffPolicy).
                        && (!link.ParentGuardian.AppUser!.HasLeft
                            || link.ParentGuardian.AppUser.Roles.Any(r => r.Role == Role.Teacher || r.Role == Role.Admin || r.Role == Role.SafeguardingOfficer)))));
        });

        modelBuilder.Entity<DbsCheck>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.TeacherUser).WithMany().HasForeignKey(x => x.TeacherUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(SensitiveDataFilter, x =>
                (_currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer))
                && _currentUser.HasMfaEnabled);
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Id);
            // ValueGeneratedNever, unchanged from before the Identity swap — but its *source*
            // changed: this Id is always copied from the paired VlmsIdentityUser row's own
            // store-generated id (Identity/AppUserProvisioningService.cs), never computed by this
            // codebase's own Max(Id)+1 convention. Shared-primary-key one-to-one *convention* with
            // VlmsIdentityUser — see AppUser's and VlmsIdentityUser's doc comments for the full
            // "why a separate table, not AppUser : IdentityUser<int>" reasoning.
            //
            // Deliberately NOT enforced as a real EF Core/database foreign key (tried, then
            // reverted — recorded here rather than silently dropped): an FK from AppUser.Id to
            // VlmsIdentityUser.Id broke every existing test in this suite that seeds a plain
            // AppUser row to represent "a user holding this role" (the overwhelming majority of
            // this codebase's ~150 tests, none of which are about Identity account creation) —
            // each would have had to additionally fabricate a matching VlmsIdentityUser row purely
            // to satisfy a constraint the test itself has nothing to do with. The integrity risk
            // this would guard against (an AppUser row with no paired credential row) is low in
            // practice: AppUserProvisioningService is the *only* code path anywhere in this
            // codebase that creates an AppUser row, and it always creates both together in one
            // transaction (see its own doc comment) — there is no other route by which an orphan
            // could occur short of a bug in that one class, which its own tests
            // (AppUserProvisioningServiceTests) directly guard against.
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        // Roles per chapter (docs/adr/0010-chapters.md): a chapter role carries its chapter, a
        // national role has none. A surrogate key, because a primary key cannot include the nullable
        // ChapterId; uniqueness is two partial indexes instead, since a plain unique index over a
        // nullable column treats every NULL as distinct (both PostgreSQL and SQLite) and would allow
        // a national role twice. UserRole carries no query filter: identity checks (sign-in, leaver
        // lockout, IdentityCurrentUserContext) must see all of a person's roles. Services that list
        // or count role holders scope to the caller's chapter explicitly (ChapterStaff).
        modelBuilder.Entity<UserRole>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasOne(x => x.User).WithMany(x => x.Roles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Chapter>().WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.UserId, x.Role, x.ChapterId }).IsUnique().HasFilter("chapter_id IS NOT NULL")
                .HasDatabaseName("IX_user_roles_user_id_role_chapter_id");
            e.HasIndex(x => new { x.UserId, x.Role }).IsUnique().HasFilter("chapter_id IS NULL")
                .HasDatabaseName("IX_user_roles_user_id_role_national");
            e.HasIndex(x => new { x.ChapterId, x.Role });
        });

        modelBuilder.Entity<Chapter>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(Chapter.MaxNameLength);
            e.Property(x => x.Place).HasMaxLength(Chapter.MaxNameLength);
            e.HasIndex(x => x.Name).IsUnique();
            // Every record that existed before chapters belongs to Shrewsbury: the migration adding
            // chapters inserts this row and gives every existing chapter-owned row chapter_id 1.
            e.HasData(new Chapter
            {
                Id = Chapter.ShrewsburyId,
                Name = "Shrewsbury Orthodox Church",
                Place = "Shrewsbury",
                CreatedAt = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true
            });
        });

        modelBuilder.Entity<ActiveChapterChoice>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Chapter>().WithMany().HasForeignKey(x => x.ChapterId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SensitiveDataAccessLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
        });

        // Guardian invite tokens (STATE.md "Identity" item 5, replacing the Microsoft Graph
        // guardian-invitation blocker — see GuardianInvite's own doc comment for the full design).
        modelBuilder.Entity<GuardianInvite>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ParentGuardian).WithMany().HasForeignKey(x => x.ParentGuardianId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ConsumedByAppUser).WithMany().HasForeignKey(x => x.ConsumedByAppUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // Parent registration applications (functional.md FR-004 as amended 22/10/2026, adr/0008
        // answers 1, 4, 5, 6). Nothing here creates a Student, link or role; see
        // Registration/StudentApplicationService.
        modelBuilder.Entity<StudentApplication>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.ApplicantUser).WithMany().HasForeignKey(x => x.ApplicantUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SafeguardingReviewedByUser).WithMany().HasForeignKey(x => x.SafeguardingReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AdminDecidedByUser).WithMany().HasForeignKey(x => x.AdminDecidedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ConsentRecord).WithMany().HasForeignKey(x => x.ConsentRecordId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.SensitiveDetails).WithOne(x => x.StudentApplication)
                .HasForeignKey<StudentApplicationSensitiveDetails>(x => x.StudentApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ApplicantUserId, x.Status });
            e.HasIndex(x => x.Status);
        });

        // StudentApplicationSensitiveDetails: same whole-entity restriction and read audit as
        // ConsentSensitiveDetails (adr/0004 as amended by adr/0006), with a narrower audience. A row
        // is visible to
        //   1. Admin/SafeguardingOfficer with MFA enabled (the two review stages); and
        //   2. the applicant themself (their own application only), so they can see and answer a
        //      request for more information. The UserId != null conjunct has the same purpose as in
        //      the ConsentSensitiveDetails parent arm: without it EF Core's null semantics would turn
        //      ApplicantUserId == UserId into a match on nothing useful, and an unresolved caller must
        //      match no row.
        // Teachers, Approvers and Parents (other than the applicant) match no arm: until approval
        // there is no student for a Teacher to teach. Writes are not governed by a query filter;
        // StudentApplicationService checks the applicant itself before changing a row.
        modelBuilder.Entity<StudentApplicationSensitiveDetails>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.StudentApplicationId).IsUnique();
            e.HasQueryFilter(SensitiveDataFilter, x =>
                ((_currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer))
                    && _currentUser.HasMfaEnabled)
                || (_currentUser.UserId != null
                    && StudentApplications.Any(a =>
                        a.Id == x.StudentApplicationId && a.ApplicantUserId == _currentUser.UserId)));
        });

        ConfigureChapters(modelBuilder);

        // Applied last, after every table/property above has its (PascalCase-default) name —
        // renames every table and column to PostgreSQL's own idiomatic snake_case convention. See
        // SnakeCaseNamingConvention's doc comment for the scope decision (tables/columns only, not
        // constraint/index names) and why this is hand-rolled rather than a third-party package.
        SnakeCaseNamingConvention.Apply(modelBuilder);
    }

    /// <summary>
    /// Chapter separation (functional.md FR-021, docs/adr/0010-chapters.md): every
    /// <see cref="IChapterOwned"/> entity gets a foreign key to <see cref="Chapter"/> and the named
    /// "Chapter" query filter, the same mechanism as adr/0004, so a query cannot return another
    /// chapter's rows whichever page or service runs it. It is a separate named filter, ANDed by
    /// EF Core with the "SensitiveData" filter on the three restricted entities, so neither weakens
    /// the other: a row must pass both.
    ///
    /// Each filter has the same three arms:
    ///   1. the system principal of the scheduled job, which sweeps every chapter
    ///      (<see cref="ICurrentUserContext.SeesAllChapters"/>; never a signed-in person);
    ///   2. the row is in the caller's active chapter (null when the caller holds no chapter role,
    ///      so it matches nothing); and
    ///   3. the row belongs to the caller themself: their own children by guardian link, their own
    ///      student record, their own guardian rows, their own registration applications. A parent
    ///      keeps their own children whichever chapter they are working in (Philip, 24/10/2026).
    ///      For everything but applications this arm applies only while the caller holds no staff
    ///      role (Teacher, Admin, Safeguarding Officer) in their active chapter
    ///      (<see cref="OwnRecordsArmApplies"/>): a Teacher in one chapter whose child is in another
    ///      must not find that child in their staff lists, or record completions for them, there.
    ///      They switch to the child's chapter, where they hold Parent. Their own applications stay
    ///      visible anywhere (an applicant holds no role in the chapter yet, so cannot switch to it);
    ///      the staff application lists scope to the active chapter themselves.
    ///      As in the adr/0004/0006 filters, each such arm requires UserId != null, because EF Core's
    ///      null semantics would otherwise turn "AppUserId == UserId" into "AppUserId IS NULL".
    /// Arm 3 is written out on each entity (not left to a nested filter on Student) so each filter
    /// can be read on its own, and none refers back to an entity whose filter refers to it: no cycle.
    /// The national administrator matches arm 2 only in a chapter where they hold a role
    /// (least privilege: running the platform is not a safeguarding role).
    /// </summary>
    private void ConfigureChapters(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Student>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (OwnRecordsArmApplies
                    && (x.AppUserId == _currentUser.UserId
                        || StudentGuardianLinks.Any(l => l.StudentId == x.Id && l.ParentGuardian!.AppUserId == _currentUser.UserId))));
        });

        modelBuilder.Entity<ParentGuardian>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (OwnRecordsArmApplies && x.AppUserId == _currentUser.UserId));
        });

        modelBuilder.Entity<StudentGuardianLink>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (OwnRecordsArmApplies && x.ParentGuardian!.AppUserId == _currentUser.UserId));
        });

        modelBuilder.Entity<StudentLessonCompletion>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (OwnRecordsArmApplies
                    && (StudentGuardianLinks.Any(l => l.StudentId == x.StudentId && l.ParentGuardian!.AppUserId == _currentUser.UserId)
                        || Students.Any(s => s.Id == x.StudentId && s.AppUserId == _currentUser.UserId))));
        });

        modelBuilder.Entity<StudentRankProgress>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (OwnRecordsArmApplies
                    && (StudentGuardianLinks.Any(l => l.StudentId == x.StudentId && l.ParentGuardian!.AppUserId == _currentUser.UserId)
                        || Students.Any(s => s.Id == x.StudentId && s.AppUserId == _currentUser.UserId))));
        });

        modelBuilder.Entity<StudentBadge>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (OwnRecordsArmApplies
                    && (StudentGuardianLinks.Any(l => l.StudentId == x.StudentId && l.ParentGuardian!.AppUserId == _currentUser.UserId)
                        || Students.Any(s => s.Id == x.StudentId && s.AppUserId == _currentUser.UserId))));
        });

        // Events: everyone working in the chapter (the calendar is for parents and students too).
        modelBuilder.Entity<ChapterEvent>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId));
        });

        modelBuilder.Entity<ClassTeacher>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId));
        });

        // Registers: the chapter's own staff only. Parents and students read marks, never registers.
        modelBuilder.Entity<AttendanceRegister>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId));
        });

        modelBuilder.Entity<AttendanceMark>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (OwnRecordsArmApplies
                    && (StudentGuardianLinks.Any(l => l.StudentId == x.StudentId && l.ParentGuardian!.AppUserId == _currentUser.UserId)
                        || Students.Any(s => s.Id == x.StudentId && s.AppUserId == _currentUser.UserId))));
        });

        modelBuilder.Entity<Certificate>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (OwnRecordsArmApplies
                    && StudentLessonCompletions.Any(c => c.Id == x.StudentLessonCompletionId
                        && (StudentGuardianLinks.Any(l => l.StudentId == c.StudentId && l.ParentGuardian!.AppUserId == _currentUser.UserId)
                            || Students.Any(s => s.Id == c.StudentId && s.AppUserId == _currentUser.UserId)))));
        });

        modelBuilder.Entity<ConsentRecord>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (OwnRecordsArmApplies
                    && (StudentGuardianLinks.Any(l => l.StudentId == x.StudentId && l.ParentGuardian!.AppUserId == _currentUser.UserId)
                        || Students.Any(s => s.Id == x.StudentId && s.AppUserId == _currentUser.UserId))));
        });

        // Arm 3 is the linked parent only: a student never reads consent details (the SensitiveData
        // filter refuses them anyway).
        modelBuilder.Entity<ConsentSensitiveDetails>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (OwnRecordsArmApplies
                    && ConsentRecords.Any(c => c.Id == x.ConsentRecordId
                        && StudentGuardianLinks.Any(l => l.StudentId == c.StudentId && l.ParentGuardian!.AppUserId == _currentUser.UserId))));
        });

        // No arm 3: a DBS check is seen only by its chapter's Admins and Safeguarding Officers (the
        // SensitiveData filter), never by the teacher it is about.
        modelBuilder.Entity<DbsCheck>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId));
        });

        // No arm 3: an invite is read by its chapter's Admins, and by the person accepting it only
        // inside GuardianInviteService's token-checked ChapterScopeOverride.
        modelBuilder.Entity<GuardianInvite>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId));
        });

        modelBuilder.Entity<StudentApplication>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (_currentUser.UserId != null && x.ApplicantUserId == _currentUser.UserId));
        });

        modelBuilder.Entity<StudentApplicationSensitiveDetails>(e =>
        {
            ChapterKey(e);
            e.HasQueryFilter(ChapterFilter, x =>
                _currentUser.SeesAllChapters
                || (_currentUser.ActiveChapterId != null && x.ChapterId == _currentUser.ActiveChapterId)
                || (_currentUser.UserId != null
                    && StudentApplications.Any(a => a.Id == x.StudentApplicationId && a.ApplicantUserId == _currentUser.UserId)));
        });
    }

    /// <summary>
    /// Whether arm 3 of the chapter filters (the caller's own children, student record and guardian
    /// rows) applies: a resolved caller who holds no staff role in their active chapter. Read by EF
    /// Core as a query parameter on every execution, like the <see cref="ICurrentUserContext"/>
    /// members it calls.
    /// </summary>
    private bool OwnRecordsArmApplies =>
        _currentUser.UserId != null
        && !_currentUser.HasRole(Role.Teacher)
        && !_currentUser.HasRole(Role.Admin)
        && !_currentUser.HasRole(Role.SafeguardingOfficer);

    private static void ChapterKey<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e)
        where T : class, IChapterOwned
    {
        // By name: a lambda over the interface-constrained T compiles to a converted member access.
        e.HasOne<Chapter>().WithMany().HasForeignKey(nameof(IChapterOwned.ChapterId)).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(nameof(IChapterOwned.ChapterId));
    }
}
