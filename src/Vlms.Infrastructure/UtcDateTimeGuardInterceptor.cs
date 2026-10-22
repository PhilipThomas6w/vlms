using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Vlms.Infrastructure;

/// <summary>
/// Refuses to save a <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is not
/// <see cref="DateTimeKind.Utc"/>, or a <see cref="DateTimeOffset"/> with a non-zero offset.
///
/// Npgsql maps both to <c>timestamp with time zone</c> and throws on those values at save time.
/// SQLite, which the test suite runs on, accepts them, so a missing <c>DateTimeKind.Utc</c> passed
/// every test and failed only on PostgreSQL (found in the browser walk, 22/10/2026: registering a
/// student wrote <c>StudentRankProgress.StartedAt</c> from <c>DateOnly.ToDateTime</c>, Kind
/// Unspecified). Running the same rule on every provider makes the tests fail where production
/// would. In production it changes nothing: Npgsql would have refused the same values.
/// </summary>
public sealed class UtcDateTimeGuardInterceptor : SaveChangesInterceptor
{
    public static readonly UtcDateTimeGuardInterceptor Instance = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Check(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            foreach (var property in entry.Properties)
            {
                // Only values this save writes: SQLite reads every DateTime back as Kind Unspecified
                // (Npgsql reads Utc), so an unchanged value re-saved on an update is not a defect.
                if (entry.State == EntityState.Modified && !property.IsModified)
                {
                    continue;
                }

                var bad = property.CurrentValue switch
                {
                    DateTime d => d.Kind != DateTimeKind.Utc,
                    DateTimeOffset o => o.Offset != TimeSpan.Zero,
                    _ => false
                };
                if (bad)
                {
                    throw new InvalidOperationException(
                        $"{entry.Metadata.ClrType.Name}.{property.Metadata.Name} must be UTC " +
                        "(DateTimeKind.Utc, or a DateTimeOffset with zero offset); PostgreSQL refuses anything else.");
                }
            }
        }
    }
}
