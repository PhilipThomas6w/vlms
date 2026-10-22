using Xunit;

namespace Vlms.Tests.Web;

/// <summary>
/// Guards the Vlms.Web side of STATE.md "Password reset + Identity hardening": Program.cs applies
/// <c>VlmsIdentityPolicy</c> and its email-link lifetime, registration no longer signs the user in
/// before their email is confirmed, and the reset/confirmation pages exist. Reads source off disk,
/// same approach as <see cref="PwaAssetsTests"/>, since Vlms.Tests doesn't reference Vlms.Web; the
/// behaviour behind the pages is tested in <c>AccountEmailServiceTests</c>.
/// </summary>
public sealed class AccountPagesWiringTests
{
    [Fact]
    public void Program_AppliesTheIdentityPolicy_AndItsEmailLinkLifetime()
    {
        var program = Read("src", "Vlms.Web", "Program.cs");

        Assert.Contains("AddIdentityCore<VlmsIdentityUser>(VlmsIdentityPolicy.Apply)", program);
        Assert.Contains("options.TokenLifespan = VlmsIdentityPolicy.EmailTokenLifespan", program);
        Assert.DoesNotContain("RequireConfirmedAccount = false", program);
    }

    // FR-016: the stamp check runs on an explicit, short interval for both static SSR (cookie) and
    // interactive circuits, rather than the framework's 30-minute default.
    [Fact]
    public void SecurityStamp_IsValidatedOnTheSameExplicitInterval_ForCookiesAndCircuits()
    {
        var program = Read("src", "Vlms.Web", "Program.cs");
        var provider = Read("src", "Vlms.Web", "Components", "Account", "IdentityRevalidatingAuthenticationStateProvider.cs");

        Assert.Contains("options.ValidationInterval = VlmsIdentityPolicy.SecurityStampValidationInterval", program);
        Assert.Contains("RevalidationInterval => VlmsIdentityPolicy.SecurityStampValidationInterval", provider);
        Assert.InRange(Vlms.Infrastructure.Identity.VlmsIdentityPolicy.SecurityStampValidationInterval,
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Register_SendsConfirmation_AndDoesNotSignIn()
    {
        var register = Read("src", "Vlms.Web", "Components", "Account", "Pages", "Register.razor");

        Assert.Contains("AccountEmails.SendConfirmationAsync(identityUser)", register);
        Assert.DoesNotContain("SignInAsync", register);
    }

    [Theory]
    [InlineData("ForgotPassword.razor", "/Account/ForgotPassword")]
    [InlineData("ResetPassword.razor", "/Account/ResetPassword")]
    [InlineData("ConfirmEmail.razor", "/Account/ConfirmEmail")]
    [InlineData("ResendEmailConfirmation.razor", "/Account/ResendEmailConfirmation")]
    public void AccountPage_ExistsAtTheRouteTheEmailsLinkTo(string file, string route)
    {
        var page = Read("src", "Vlms.Web", "Components", "Account", "Pages", file);

        Assert.Contains($"@page \"{route}\"", page);
        Assert.DoesNotContain("@rendermode", page);
    }

    [Fact]
    public void Login_LinksToResetAndResend_AndExplainsUnconfirmedAccounts()
    {
        var login = Read("src", "Vlms.Web", "Components", "Account", "Pages", "Login.razor");

        Assert.Contains("href=\"Account/ForgotPassword\"", login);
        Assert.Contains("href=\"Account/ResendEmailConfirmation\"", login);
        Assert.Contains("result.IsNotAllowed", login);
    }

    // FR-011, adr/0007: students sign in with a username, not an email, on the same page, and the
    // confirmation rule that admits them is the registered one.
    [Fact]
    public void Login_AcceptsAStudentUsername_AndProgramRegistersTheStudentConfirmationRule()
    {
        var login = Read("src", "Vlms.Web", "Components", "Account", "Pages", "Login.razor");
        var program = Read("src", "Vlms.Web", "Program.cs");

        Assert.DoesNotContain("[EmailAddress]", login);
        Assert.Contains("PasswordSignInAsync(", login);
        Assert.Contains("Input.UserName", login);
        Assert.Contains(".AddUserConfirmation<VlmsUserConfirmation>()", program);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

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
