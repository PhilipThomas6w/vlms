using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vlms.Domain;
using Vlms.Infrastructure.Authorization;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// One student and their sign-in, if any, for the parent and Admin sign-in pages.
/// <paramref name="SignInDisabled"/> is true when an Admin has turned the sign-in off.
/// </summary>
public sealed record StudentSignInSummary(int StudentId, string StudentName, string? Username, bool SignInDisabled = false);

/// <summary>Outcome of creating a sign-in or resetting its password: success, or the reasons it failed validation.</summary>
public sealed record StudentSignInResult(bool Succeeded, IReadOnlyList<string> Errors)
{
    public static StudentSignInResult Success { get; } = new(true, []);

    public static StudentSignInResult Failed(params string[] errors) => new(false, errors);

    internal static StudentSignInResult From(IdentityResult result) =>
        result.Succeeded ? Success : new(false, result.Errors.Select(e => e.Description).ToList());
}

/// <summary>
/// Student sign-ins (functional.md FR-011, adr/0007-student-sign-in-accounts.md; Philip, 22/10/2026).
/// A parent creates a username and password, with no email address, for each child linked to them,
/// and can reset that password. An Admin can also reset it. This is the only code that grants
/// <see cref="Role.Student"/> (<see cref="RoleAssignmentService"/> refuses to), and the only code
/// that sets <see cref="Student.AppUserId"/>.
///
/// <b>Who may act</b>, checked here as well as by the page policy:
/// <list type="bullet">
/// <item>Create: a <see cref="Role.Parent"/> caller who has not left (<see cref="AppUser.HasLeft"/>)
/// and is linked to the student through <see cref="StudentGuardianLink"/>
/// (<see cref="ParentGuardianLinkage"/>). Admins do not create sign-ins.</item>
/// <item>Reset: the same linked parent, or an <see cref="Role.Admin"/> with MFA enabled.</item>
/// <item>Disable and restore: an <see cref="Role.Admin"/> with MFA enabled only.</item>
/// </list>
/// Refusals throw <see cref="UnauthorizedAccessException"/>; a second sign-in for the same student
/// throws <see cref="InvalidOperationException"/>; a bad username or password returns
/// <see cref="StudentSignInResult.Failed"/>.
///
/// <b>Atomic.</b> Creating the paired Identity and <see cref="AppUser"/> rows, granting the role and
/// setting <see cref="Student.AppUserId"/> commit together or not at all. The link is set with a
/// conditional update (<c>WHERE app_user_id IS NULL</c>), so two parents creating a sign-in for the
/// same child at once cannot both succeed.
///
/// <b>Reset</b> uses Identity's own reset-token path, which validates the new password before
/// changing anything (<see cref="VlmsPasswordValidator"/>: 8+ characters for a student) and rotates
/// the security stamp, so the student's existing sessions end at the next revalidation. A
/// successful reset also clears any lockout and the failed-attempt count (Philip, 22/10/2026). It
/// refuses an account that has an email address: a student sign-in never has one, so a parent can
/// never reset an adult's password through this route. It does not touch
/// <see cref="VlmsIdentityUser.SignInDisabled"/>: a parent's reset can't undo an Admin's disable.
///
/// <b>Disable and restore</b> (Philip, 22/10/2026) set or clear
/// <see cref="VlmsIdentityUser.SignInDisabled"/> and rotate the security stamp in the same save, so
/// a disabled student can't sign in (<see cref="VlmsUserConfirmation"/>) and their open sessions,
/// including a long-lived "stay signed in" cookie, end at the next revalidation. Leaving (Inactive
/// or Graduated) does not disable a sign-in.
/// </summary>
public sealed class StudentAccountService
{
    private readonly VlmsDbContext _db;
    private readonly ICurrentUserContext _currentUser;
    private readonly UserManager<VlmsIdentityUser> _userManager;
    private readonly AppUserProvisioningService _provisioning;

    public StudentAccountService(
        VlmsDbContext db,
        ICurrentUserContext currentUser,
        UserManager<VlmsIdentityUser> userManager,
        AppUserProvisioningService provisioning)
    {
        _db = db;
        _currentUser = currentUser;
        _userManager = userManager;
        _provisioning = provisioning;
    }

    /// <summary>
    /// The students the caller may manage sign-ins for: a parent's own linked children, ordered by
    /// name. Parent only (Admins use <see cref="ListWithSignInAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<StudentSignInSummary>> ListForParentAsync(CancellationToken ct = default)
    {
        var parentUserId = await RequireActiveParentAsync(ct);
        var linkedIds = ParentGuardianLinkage.LinkedStudentIds(_db, parentUserId);
        return await SummariesAsync(_db.Students.Where(s => linkedIds.Contains(s.Id)), ct);
    }

    /// <summary>Every student who has a sign-in, ordered by name, for the Admin reset page. Admin only.</summary>
    public async Task<IReadOnlyList<StudentSignInSummary>> ListWithSignInAsync(CancellationToken ct = default)
    {
        RequireAdmin();
        return await SummariesAsync(_db.Students.Where(s => s.AppUserId != null), ct);
    }

    /// <summary>Creates the sign-in for <paramref name="studentId"/>. Linked, non-leaver Parent only.</summary>
    public async Task<StudentSignInResult> CreateSignInAsync(
        int studentId, string username, string password, CancellationToken ct = default)
    {
        var parentUserId = await RequireActiveParentAsync(ct);
        await RequireLinkedAsync(studentId, parentUserId, ct);

        var student = await _db.Students.AsNoTracking().SingleOrDefaultAsync(s => s.Id == studentId, ct)
            ?? throw new InvalidOperationException($"Student {studentId} does not exist.");
        if (student.AppUserId is not null)
        {
            throw new InvalidOperationException($"{student.Name} already has a sign-in. Reset its password instead.");
        }

        username = (username ?? "").Trim();
        if (!StudentUsername.IsValid(username))
        {
            return StudentSignInResult.Failed(StudentUsername.RuleDescription);
        }

        if (string.IsNullOrEmpty(password))
        {
            return StudentSignInResult.Failed("Enter a password.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var (result, _, appUser) = await _provisioning.CreateStudentSignInAsync(username, password, student.Name, ct);
        if (!result.Succeeded)
        {
            return StudentSignInResult.From(result);
        }

        // Student is held in the student's own chapter (FR-021, docs/adr/0010-chapters.md).
        _db.UserRoles.Add(new UserRole { UserId = appUser!.Id, Role = Role.Student, ChapterId = student.ChapterId });
        await _db.SaveChangesAsync(ct);

        var linked = await _db.Students
            .Where(s => s.Id == studentId && s.AppUserId == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.AppUserId, appUser.Id), ct);
        if (linked != 1)
        {
            // Another request linked a sign-in first; the transaction is not committed.
            throw new InvalidOperationException($"{student.Name} already has a sign-in. Reset its password instead.");
        }

        await transaction.CommitAsync(ct);
        return StudentSignInResult.Success;
    }

    /// <summary>
    /// Sets a new password on <paramref name="studentId"/>'s sign-in. A linked, non-leaver Parent or
    /// an Admin with MFA. Throws if the student has no sign-in.
    /// </summary>
    public async Task<StudentSignInResult> ResetPasswordAsync(int studentId, string newPassword, CancellationToken ct = default)
    {
        if (_currentUser.HasRole(Role.Admin))
        {
            RequireAdmin();
        }
        else
        {
            var parentUserId = await RequireActiveParentAsync(ct);
            await RequireLinkedAsync(studentId, parentUserId, ct);
        }

        var identityUser = await FindStudentAccountAsync(studentId, ct);

        if (string.IsNullOrEmpty(newPassword))
        {
            return StudentSignInResult.Failed("Enter a password.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var token = await _userManager.GeneratePasswordResetTokenAsync(identityUser);
        var reset = await _userManager.ResetPasswordAsync(identityUser, token, newPassword);
        if (!reset.Succeeded)
        {
            return StudentSignInResult.From(reset);
        }

        // A parent's or Admin's reset clears the lockout (Philip, 22/10/2026); the adult self-service
        // reset does not. SignInDisabled is deliberately left as it is.
        if (await _userManager.GetLockoutEnabledAsync(identityUser))
        {
            ThrowIfFailed(await _userManager.SetLockoutEndDateAsync(identityUser, null), "clear the lockout");
        }

        ThrowIfFailed(await _userManager.ResetAccessFailedCountAsync(identityUser), "reset the failed sign-in count");

        await transaction.CommitAsync(ct);
        return StudentSignInResult.Success;
    }

    /// <summary>
    /// Turns <paramref name="studentId"/>'s sign-in off, so the student can't sign in and their open
    /// sessions end. Admin with MFA only. A no-op if it is already off.
    /// </summary>
    public Task DisableSignInAsync(int studentId, CancellationToken ct = default) =>
        SetSignInDisabledAsync(studentId, disabled: true, ct);

    /// <summary>Turns <paramref name="studentId"/>'s sign-in back on. Admin with MFA only. A no-op if it is already on.</summary>
    public Task RestoreSignInAsync(int studentId, CancellationToken ct = default) =>
        SetSignInDisabledAsync(studentId, disabled: false, ct);

    private async Task SetSignInDisabledAsync(int studentId, bool disabled, CancellationToken ct)
    {
        RequireAdmin();

        var identityUser = await FindStudentAccountAsync(studentId, ct);
        if (identityUser.SignInDisabled == disabled)
        {
            return;
        }

        identityUser.SignInDisabled = disabled;
        // Rotates the stamp and saves the flag in one update: existing sessions end at their next
        // revalidation whichever way the flag moved.
        ThrowIfFailed(
            await _userManager.UpdateSecurityStampAsync(identityUser),
            disabled ? "disable the sign-in" : "restore the sign-in");
    }

    private async Task<VlmsIdentityUser> FindStudentAccountAsync(int studentId, CancellationToken ct)
    {
        var appUserId = await _db.Students.Where(s => s.Id == studentId).Select(s => s.AppUserId).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Student {studentId} does not have a sign-in yet.");

        var identityUser = await _userManager.FindByIdAsync(appUserId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? throw new InvalidOperationException($"Student {studentId}'s sign-in account is missing.");
        if (!StudentSignInPolicy.IsStudentAccount(identityUser))
        {
            throw new InvalidOperationException(
                $"Student {studentId}'s account has an email address, so it is not a student sign-in and can't be changed here.");
        }

        return identityUser;
    }

    private static void ThrowIfFailed(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {action}: {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }
    }

    private async Task<IReadOnlyList<StudentSignInSummary>> SummariesAsync(IQueryable<Student> students, CancellationToken ct)
    {
        var rows = await students
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.AppUserId })
            .ToListAsync(ct);

        var accountIds = rows.Where(r => r.AppUserId != null).Select(r => r.AppUserId!.Value).ToList();
        var accounts = await _db.Users
            .Where(u => accountIds.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName, u.SignInDisabled })
            .ToDictionaryAsync(u => u.Id, ct);

        return rows
            .Select(r => r.AppUserId is int id && accounts.TryGetValue(id, out var account)
                ? new StudentSignInSummary(r.Id, r.Name, account.UserName, account.SignInDisabled)
                : new StudentSignInSummary(r.Id, r.Name, null))
            .ToList();
    }

    private async Task<int> RequireActiveParentAsync(CancellationToken ct)
    {
        if (!_currentUser.HasRole(Role.Parent) || _currentUser.UserId is not int userId)
        {
            throw new UnauthorizedAccessException("Only a parent linked to the student can manage their sign-in.");
        }

        return userId;
    }

    private async Task RequireLinkedAsync(int studentId, int parentUserId, CancellationToken ct)
    {
        if (!await ParentGuardianLinkage.IsLinkedAsync(_db, studentId, parentUserId, ct))
        {
            throw new UnauthorizedAccessException("Only a parent linked to the student can manage their sign-in.");
        }
    }

    private void RequireAdmin()
    {
        if (!_currentUser.HasRole(Role.Admin) || !_currentUser.HasMfaEnabled)
        {
            throw new UnauthorizedAccessException("Caller must be an Admin with two-factor authentication enabled.");
        }
    }
}
