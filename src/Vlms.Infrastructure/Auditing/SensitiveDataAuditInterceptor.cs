using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vlms.Domain;

namespace Vlms.Infrastructure.Auditing;

/// <summary>
/// Writes a <see cref="SensitiveDataAccessLog"/> row for every <see cref="DbsCheck"/> or
/// <see cref="ConsentSensitiveDetails"/> instance EF Core materializes, per
/// adr/0004-sensitive-data-access-control.md.
///
/// <see cref="IMaterializationInterceptor"/> is registered as a singleton shared across
/// <see cref="VlmsDbContext"/> instances, so the current-user context is resolved per call via
/// <c>materializationData.Context.GetService&lt;T&gt;()</c>, never cached on the interceptor.
/// <see cref="InitializedInstance"/> fires once per row EF materializes from a query — including
/// once per row across a multi-row read — giving direct typed access to the entity's id, unlike a
/// <see cref="IDbCommandInterceptor"/> which only sees raw SQL/a forward-only reader (see the ADR's
/// Alternatives section for why that mechanism was rejected).
///
/// The audit row is written through a brand-new <see cref="VlmsDbContext"/> instance (its own
/// connection/change tracker) rather than added to the tracker of the context that is still
/// mid-enumeration of the query being audited — EF Core's reentrancy guard rejects a second
/// operation started on the same context instance before the first has completed, so the write
/// cannot go through that same context.
/// </summary>
public sealed class SensitiveDataAuditInterceptor : IMaterializationInterceptor
{
    public static readonly SensitiveDataAuditInterceptor Instance = new();

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        var (entityName, entityId) = entity switch
        {
            DbsCheck dbsCheck => (nameof(DbsCheck), (int?)dbsCheck.Id),
            ConsentSensitiveDetails details => (nameof(ConsentSensitiveDetails), (int?)details.Id),
            _ => (null, null)
        };

        if (entityName is not null && entityId is not null)
        {
            WriteAuditEntry(materializationData.Context, entityName, entityId.Value);
        }

        return entity;
    }

    private static void WriteAuditEntry(DbContext? sourceContext, string entityName, int entityId)
    {
        if (sourceContext is null)
        {
            return;
        }

        var options = sourceContext.GetService<DbContextOptions<VlmsDbContext>>();
        var currentUser = sourceContext.GetService<ICurrentUserContext>();

        using var auditContext = new VlmsDbContext(options, currentUser);
        auditContext.SensitiveDataAccessLogs.Add(new SensitiveDataAccessLog
        {
            Id = 0,
            UserId = currentUser.UserId,
            Entity = entityName,
            EntityId = entityId,
            AccessedAt = DateTime.UtcNow,
            AccessType = SensitiveAccessType.View
        });
        auditContext.SaveChanges();
    }
}
