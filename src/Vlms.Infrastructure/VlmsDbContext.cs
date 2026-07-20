using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Auditing;
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
    public DbSet<GuardianInvite> GuardianInvites => Set<GuardianInvite>();
    public DbSet<SensitiveDataAccessLog> SensitiveDataAccessLogs => Set<SensitiveDataAccessLog>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(SensitiveDataAuditInterceptor.Instance);
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

        // Whole-entity sensitive-data restriction (adr/0004 §"MFA-gated roles", pass 7 — STATE.md
        // "Identity" item): only Admin/SafeguardingOfficer see rows, AND ONLY once they have TOTP
        // MFA enabled (ICurrentUserContext.HasMfaEnabled). This is a genuine strengthening added
        // alongside the identity-provider swap, not a mechanical rename — MFA enforcement for these
        // two roles (item 4 of that STATE.md item) is only meaningful if it also protects the exact
        // entities ADR-0004 exists to protect, not just the Razor pages that display them; without
        // this, an Admin who never enrolled MFA could still be denied the /safeguarding/* pages by
        // MfaRequirementHandler yet still read DbsCheck/ConsentSensitiveDetails through any other
        // code path that queries VlmsDbContext directly. See openwiki/access-control.md's MFA
        // section for the full reasoning and openwiki/authentication-authorization.md for the
        // enforced-for-these-two-roles-only, offered-to-everyone scope decision.
        modelBuilder.Entity<ConsentSensitiveDetails>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.ConsentRecordId).IsUnique();
            e.HasQueryFilter(x =>
                (_currentUser.HasRole(Role.Admin) || _currentUser.HasRole(Role.SafeguardingOfficer))
                && _currentUser.HasMfaEnabled);
        });

        modelBuilder.Entity<DbsCheck>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.TeacherUser).WithMany().HasForeignKey(x => x.TeacherUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x =>
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

        // Applied last, after every table/property above has its (PascalCase-default) name —
        // renames every table and column to PostgreSQL's own idiomatic snake_case convention. See
        // SnakeCaseNamingConvention's doc comment for the scope decision (tables/columns only, not
        // constraint/index names) and why this is hand-rolled rather than a third-party package.
        SnakeCaseNamingConvention.Apply(modelBuilder);
    }
}
