using Vlms.Infrastructure.Storage;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// <see cref="BlobStorageRoot"/>: Production refuses a blank, relative or in-app-directory storage
/// root (a release's <c>rsync --delete</c> would wipe it); Development keeps the local default.
/// Plus a source check that Program.cs resolves the root at startup with the Production flag.
/// </summary>
public sealed class BlobStorageRootTests
{
    private static readonly string AppDir = Path.Combine(Path.GetTempPath(), "vlms-root-tests", "web");
    private static readonly string BaseDir = Path.Combine(Path.GetTempPath(), "vlms-root-tests", "bin");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("App_Data/blobs")]
    public void Production_BlankOrRelativeRoot_FailsFast(string? configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            BlobStorageRoot.Resolve(configured, requireExternalRoot: true, AppDir, BaseDir));
        Assert.Contains(BlobStorageRoot.RootPathKey, ex.Message);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("web/App_Data/blobs")]
    [InlineData("bin/blobs")]
    public void Production_RootInsideAppDirectory_FailsFast(string relativeToTestRoot)
    {
        var configured = Path.Combine(Path.GetTempPath(), "vlms-root-tests", relativeToTestRoot);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BlobStorageRoot.Resolve(configured, requireExternalRoot: true, AppDir, BaseDir));
        Assert.Contains("inside the app directory", ex.Message);
    }

    [Theory]
    [InlineData("blobs")]
    [InlineData("web-blobs")]
    public void Production_RootOutsideAppDirectory_IsAccepted(string relativeToTestRoot)
    {
        var configured = Path.Combine(Path.GetTempPath(), "vlms-root-tests", relativeToTestRoot);

        Assert.Equal(Path.GetFullPath(configured),
            BlobStorageRoot.Resolve(configured, requireExternalRoot: true, AppDir, BaseDir));
    }

    [Fact]
    public void Development_BlankRoot_UsesLocalDefault()
    {
        Assert.Equal(Path.GetFullPath(LocalDiskBlobStorage.DefaultRootPath),
            BlobStorageRoot.Resolve(null, requireExternalRoot: false, AppDir, BaseDir));
    }

    [Fact]
    public void Program_ResolvesRootAtStartup_RequiredInProduction()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Vlms.Web", "Program.cs"));

        Assert.Contains("BlobStorageRoot.Resolve(", program);
        Assert.Contains("requireExternalRoot: builder.Environment.IsProduction()", program);
        Assert.Contains("new LocalDiskBlobStorage(blobRootPath)", program);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vlms.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate repository root (Vlms.slnx) from test output directory");
        return directory!.FullName;
    }
}
