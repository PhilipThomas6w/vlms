using Vlms.Domain;

namespace Vlms.Infrastructure.Security;

/// <summary>
/// <see cref="ICurrentUserContext"/> for <see cref="Safeguarding.ConsentExpiryJob"/> (the WebJob,
/// adr/0003-scheduled-jobs-webjobs.md), which runs on a schedule with no signed-in human caller.
///
/// Grants exactly the two roles VlmsDbContext's sensitive-data query filters check for — Admin and
/// SafeguardingOfficer (adr/0004-sensitive-data-access-control.md) — because the job's daily sweep
/// must legitimately read <see cref="DbsCheck"/> rows to do its job, and nothing else; it is not a
/// general "system can do anything" bypass. <see cref="UserId"/> is null, which
/// <see cref="ICurrentUserContext.UserId"/>'s own doc comment already anticipates ("or null if
/// unresolved (e.g. a background job)") and which <see cref="SensitiveDataAccessLog.UserId"/>
/// already models as a valid, resolvable-caller-absent case — the job's reads of
/// <see cref="DbsCheck"/> still write an audit row (via the existing
/// <see cref="Auditing.SensitiveDataAuditInterceptor"/>, unchanged), just with a null UserId, the
/// same as any other caller Entra couldn't resolve.
///
/// Never wired into Vlms.Web — Program.cs continues to resolve <see cref="EntraCurrentUserContext"/>
/// for the interactive app. This is only for the WebJob host (Vlms.Jobs).
/// </summary>
public sealed class SystemCurrentUserContext : ICurrentUserContext
{
    public static readonly SystemCurrentUserContext Instance = new();

    public int? UserId => null;

    public bool HasRole(Role role) => role is Role.Admin or Role.SafeguardingOfficer;
}
