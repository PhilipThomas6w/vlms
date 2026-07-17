using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Auditing;

namespace Vlms.Infrastructure;

/// <summary>
/// EF Core context for VLMS (Azure SQL Database, adr/0001-technology-stack.md). Applies the
/// whole-entity sensitive-data restriction and read-audit logging from
/// adr/0004-sensitive-data-access-control.md.
/// </summary>
public sealed class VlmsDbContext : DbContext
{
    private readonly ICurrentUserContext _currentUser;

    public VlmsDbContext(DbContextOptions<VlmsDbContext> options, ICurrentUserContext currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<Rank> Ranks => Set<Rank>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<LessonChangeProposal> LessonChangeProposals => Set<LessonChangeProposal>();
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
    public DbSet<SensitiveDataAccessLog> SensitiveDataAccessLogs => Set<SensitiveDataAccessLog>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(SensitiveDataAuditInterceptor.Instance);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every Id/composite key below is application-assigned (ValueGeneratedNever) rather than
        // database identity — deliberately simple for a solo-maintained, tens-of-users system, and
        // it keeps the read-audit tests below fully deterministic.

        modelBuilder.Entity<Rank>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasMany(x => x.Lessons).WithOne(x => x.Rank).HasForeignKey(x => x.RankId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.RankBadges).WithOne(x => x.Rank).HasForeignKey(x => x.RankId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Lesson>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasMany(x => x.ChangeProposals).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Completions).WithOne(x => x.Lesson).HasForeignKey(x => x.LessonId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LessonChangeProposal>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.ProposedByUser).WithMany().HasForeignKey(x => x.ProposedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ApproverUser).WithMany().HasForeignKey(x => x.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ResubmissionOfProposal).WithMany().HasForeignKey(x => x.ResubmissionOfProposalId).OnDelete(DeleteBehavior.Restrict);
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
        });

        modelBuilder.Entity<StudentBadge>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.RankBadge).WithMany(x => x.StudentBadges).HasForeignKey(x => x.RankBadgeId).OnDelete(DeleteBehavior.Restrict);
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
            e.HasOne(x => x.SensitiveDetails).WithOne(x => x.ConsentRecord)
                .HasForeignKey<ConsentSensitiveDetails>(x => x.ConsentRecordId).OnDelete(DeleteBehavior.Cascade);
        });

        // Whole-entity sensitive-data restriction (adr/0004): only Admin/SafeguardingOfficer see rows.
        modelBuilder.Entity<ConsentSensitiveDetails>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.ConsentRecordId).IsUnique();
            e.HasQueryFilter(x => _currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer));
        });

        modelBuilder.Entity<DbsCheck>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.TeacherUser).WithMany().HasForeignKey(x => x.TeacherUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => _currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer));
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Role });
            e.HasOne(x => x.User).WithMany(x => x.Roles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SensitiveDataAccessLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
        });
    }
}
