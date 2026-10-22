using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Guards the .NET 10 migration of <c>IdentityRedirectManager</c>: with
/// <c>BlazorDisableThrowNavigationException</c> set, <c>NavigateTo</c> no longer throws during static
/// SSR, so the template's trailing InvalidOperationException fired on every redirect and every
/// sign-in answered 500.
/// </summary>
public sealed class IdentityRedirectManagerTests
{
    [Fact]
    public void RedirectManager_DoesNotThrowAfterNavigating_WhenNavigationExceptionsAreDisabled()
    {
        var csproj = Read("src", "Vlms.Web", "Vlms.Web.csproj");
        var manager = Read("src", "Vlms.Web", "Components", "Account", "IdentityRedirectManager.cs");

        Assert.Contains("<BlazorDisableThrowNavigationException>true</BlazorDisableThrowNavigationException>", csproj);
        Assert.DoesNotContain("can only be used during static rendering", manager);
        Assert.DoesNotContain("[DoesNotReturn]", manager);
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build", "verify.ps1")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }
}
