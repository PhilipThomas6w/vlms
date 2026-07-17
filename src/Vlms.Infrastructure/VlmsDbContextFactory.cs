using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vlms.Infrastructure.Security;

namespace Vlms.Infrastructure;

/// <summary>
/// Design-time factory so `dotnet ef migrations add`/`dotnet ef database update` can construct
/// a <see cref="VlmsDbContext"/> without needing the full Vlms.Web host. Uses a local/test
/// connection string placeholder — real Azure SQL credentials are never wired here
/// (adr/0001-technology-stack.md, VISION.md hard constraints).
/// </summary>
public sealed class VlmsDbContextFactory : IDesignTimeDbContextFactory<VlmsDbContext>
{
    public VlmsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<VlmsDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=(localdb)\\mssqllocaldb;Database=VlmsLocalDev;Trusted_Connection=True;TrustServerCertificate=True");

        return new VlmsDbContext(optionsBuilder.Options, new NullCurrentUserContext());
    }
}
