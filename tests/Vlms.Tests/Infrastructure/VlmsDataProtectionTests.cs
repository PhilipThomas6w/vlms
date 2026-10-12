using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vlms.Infrastructure.Security;
using Xunit;

namespace Vlms.Tests.Infrastructure;

public sealed class VlmsDataProtectionTests : IDisposable
{
    private readonly string _keysPath = Path.Combine(Path.GetTempPath(), "vlms-dp-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConfiguredKeysPath_PersistsKeyRingToThatDirectory()
    {
        using var provider = BuildProvider(_keysPath, requireKeysPath: true);

        var repository = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;

        var fileRepository = Assert.IsType<FileSystemXmlRepository>(repository);
        Assert.Equal(Path.GetFullPath(_keysPath), fileRepository.Directory.FullName.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ApplicationName_IsFixed_NotDerivedFromContentRoot()
    {
        using var provider = BuildProvider(_keysPath, requireKeysPath: true);

        Assert.Equal(VlmsDataProtection.ApplicationName,
            provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
    }

    [Fact]
    public void PayloadProtectedBeforeRestart_UnprotectsAfterRestart()
    {
        string token;
        using (var before = BuildProvider(_keysPath, requireKeysPath: true))
        {
            token = before.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Vlms.Tests").ToTimeLimitedDataProtector()
                .Protect("invite-id", TimeSpan.FromDays(1));
        }

        using var after = BuildProvider(_keysPath, requireKeysPath: true);
        var unprotected = after.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Vlms.Tests").ToTimeLimitedDataProtector()
            .Unprotect(token);

        Assert.Equal("invite-id", unprotected);
    }

    [Fact]
    public void MissingKeysPath_WhenRequired_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildProvider(keysPath: null, requireKeysPath: true));
        Assert.Contains(VlmsDataProtection.KeysPathKey, ex.Message);
    }

    [Fact]
    public void MissingKeysPath_WhenNotRequired_UsesFrameworkDefault()
    {
        using var provider = BuildProvider(keysPath: null, requireKeysPath: false);

        Assert.Null(provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
    }

    private static ServiceProvider BuildProvider(string? keysPath, bool requireKeysPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [VlmsDataProtection.KeysPathKey] = keysPath })
            .Build();

        var services = new ServiceCollection();
        services.AddVlmsDataProtection(configuration, requireKeysPath);
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        if (Directory.Exists(_keysPath))
        {
            Directory.Delete(_keysPath, recursive: true);
        }
    }
}
