using Microsoft.AspNetCore.Authorization;

namespace Vlms.Infrastructure.Authorization;

/// <summary>
/// STATE.md "Identity" item 4's MFA-enforcement scope decision, made explicit: TOTP multi-factor
/// authentication is **enforced for the Admin, SafeguardingOfficer and Teacher roles specifically —
/// globally, for every authorized action, not just the safeguarding-sensitive pages** — and merely
/// **offered** (available to enrol, never required) for every other role (Parent, Student,
/// Approver). Teacher was added on 22/10/2026 (Philip; adr/0006 amendment, functional.md FR-015)
/// because since adr/0006 Teachers can read <c>ConsentSensitiveDetails</c> (medical, dietary/SEN,
/// emergency contacts). "Globally" is the deliberate part: a caller who holds one of these roles
/// at all is blocked from every policy-gated page and resource until they enable MFA, even a page
/// a different role they also hold would otherwise satisfy on its own — the simpler, more
/// conservative rule to reason about and to state ("if you hold a role that can see
/// safeguarding-sensitive data, MFA is mandatory, full stop") than "MFA is required only on the
/// specific policy that happens to need that particular role today". See
/// <see cref="MfaRequirementHandler"/> for the check itself, and
/// <c>VlmsDbContext.OnModelCreating</c>'s <c>DbsCheck</c>/<c>ConsentSensitiveDetails</c> query
/// filters for the matching defense-in-depth enforcement at the data layer (adr/0004
/// §"MFA-gated roles", pass 7; adr/0006 for the Teacher arm) — the same "not just page-level
/// gating" discipline this codebase already applies to every other authorization decision. Added
/// to every ASP.NET Core authorization policy in Vlms.Web's Program.cs (the per-role policies, both
/// `AnyRole` policies, and `StudentAccess`), not just the role-specific ones. The enrolment pages
/// (<c>Account/Manage/TwoFactorAuthentication</c>, <c>Account/Manage/EnableAuthenticator</c>) carry
/// plain <c>[Authorize]</c> only, so an MFA-gated caller can always reach them.
/// </summary>
public sealed class MfaRequirement : IAuthorizationRequirement
{
}
