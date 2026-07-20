using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vlms.Infrastructure.Security;

namespace Vlms.Infrastructure;

/// <summary>
/// Design-time factory so `dotnet ef migrations add`/`dotnet ef database update` can construct
/// a <see cref="VlmsDbContext"/> without needing the full Vlms.Web host. Uses a local/test
/// connection string placeholder — real PostgreSQL credentials are never wired here
/// (adr/0005-hosting-pivot-self-hosted-vps-postgresql.md, VISION.md hard constraints).
/// </summary>
public sealed class VlmsDbContextFactory : IDesignTimeDbContextFactory<VlmsDbContext>
{
    public VlmsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<VlmsDbContext>();
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=vlms_local_dev;Username=vlms_local_dev;Password=PLACEHOLDER");

        return new VlmsDbContext(optionsBuilder.Options, new NullCurrentUserContext());
    }
}
