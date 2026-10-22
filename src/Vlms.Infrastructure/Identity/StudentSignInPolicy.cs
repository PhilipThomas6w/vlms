using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vlms.Infrastructure.Identity;

/// <summary>
/// The rules that differ for a student sign-in (functional.md FR-011, adr/0007-student-sign-in-accounts.md,
/// Philip's decisions of 22/10/2026). Every other rule, and every rule for adults, is
/// <see cref="VlmsIdentityPolicy"/>'s.
///
/// <b>What a student account is.</b> An account with no email address. Only
/// <see cref="AppUserProvisioningService.CreateStudentSignInAsync"/> creates one; every adult account's
/// user name is its email (<see cref="AppUserProvisioningService.CreateAsync"/> refuses one without
/// <c>@</c>), and no page lets a user remove their email. The same test decides confirmation
/// (<see cref="VlmsUserConfirmation"/>), so the two can't disagree.
/// </summary>
public static class StudentSignInPolicy
{
    /// <summary>A student password is at least 8 characters, with no upper-case, digit or symbol rule.</summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>
    /// How long a student stays signed in on their own device without using the app. The cookie is
    /// persistent and its expiry slides: each use renews it for another 90 days (the cookie
    /// handler renews once half the window has passed). Revocation does not wait for it: a reset,
    /// disable or restore rotates the security stamp, and the stamp is rechecked every
    /// <see cref="VlmsIdentityPolicy.SecurityStampValidationInterval"/>.
    /// </summary>
    public static readonly TimeSpan PersistentSignInLifetime = TimeSpan.FromDays(90);

    public const string PasswordRuleDescription = "A student password must be at least 8 characters. Capital letters, numbers and symbols are optional.";

    /// <summary>True for a student sign-in: an account with no email address (see the class comment).</summary>
    public static bool IsStudentAccount(VlmsIdentityUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return string.IsNullOrEmpty(user.Email);
    }

    /// <summary>
    /// Replaces Identity's default <see cref="PasswordValidator{TUser}"/> with
    /// <see cref="VlmsPasswordValidator"/>. Call after <c>AddIdentityCore</c>, which registers the
    /// default; the result is exactly one password validator.
    /// </summary>
    public static IdentityBuilder AddVlmsPasswordRules(this IdentityBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Scoped<IPasswordValidator<VlmsIdentityUser>, VlmsPasswordValidator>());
        return builder;
    }
}

/// <summary>
/// The password rule (adr/0007): for an adult account, Identity's own
/// <see cref="PasswordValidator{TUser}"/> with the global <see cref="IdentityOptions.Password"/>
/// options, which are left at the framework defaults (6+ characters, upper, lower, digit, symbol):
/// the same code and options as before, so adult registration, reset and change are unchanged. For a
/// student account (<see cref="StudentSignInPolicy.IsStudentAccount"/>): at least
/// <see cref="StudentSignInPolicy.MinimumPasswordLength"/> characters and not all whitespace, nothing
/// else. Identity calls this for every create, reset and change, with the user being changed.
/// </summary>
public sealed class VlmsPasswordValidator : PasswordValidator<VlmsIdentityUser>
{
    public VlmsPasswordValidator(IdentityErrorDescriber? errors = null)
        : base(errors)
    {
    }

    public override Task<IdentityResult> ValidateAsync(UserManager<VlmsIdentityUser> manager, VlmsIdentityUser user, string? password)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!StudentSignInPolicy.IsStudentAccount(user))
        {
            return base.ValidateAsync(manager, user, password);
        }

        return Task.FromResult(
            string.IsNullOrWhiteSpace(password) || password.Length < StudentSignInPolicy.MinimumPasswordLength
                ? IdentityResult.Failed(Describer.PasswordTooShort(StudentSignInPolicy.MinimumPasswordLength))
                : IdentityResult.Success);
    }
}
