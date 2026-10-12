using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Vlms.Infrastructure.Notifications;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// Verifies <see cref="GraphEmailSender"/> against a scripted HTTP handler: the client-credentials
/// token request, the sendMail request shape, token caching and refresh, and that every failure
/// throws (so <see cref="NotificationService"/> retries/escalates). Plus the <c>GraphMail</c>
/// configuration binding both hosts use.
/// </summary>
public sealed class GraphEmailSenderTests
{
    private static readonly GraphEmailOptions Options = new(
        "tenant-123", "client-456", "secret-789", "noreply@varangians.co.uk");

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Auth, string Body)> Requests { get; } = new();
        public Queue<HttpResponseMessage> TokenResponses { get; } = new();
        public Queue<HttpResponseMessage> SendResponses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            var queue = request.RequestUri!.Host == "login.microsoftonline.com" ? TokenResponses : SendResponses;
            return queue.Count > 0 ? queue.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HttpResponseMessage Token(string value, int expiresIn = 3600) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"token_type":"Bearer","expires_in":{{expiresIn}},"access_token":"{{value}}"}""", System.Text.Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Accepted() => new(HttpStatusCode.Accepted);

    [Fact]
    public async Task SendAsync_RequestsAClientCredentialsToken_ForTheConfiguredTenant()
    {
        var handler = new ScriptedHandler();
        handler.TokenResponses.Enqueue(Token("tok-1"));
        handler.SendResponses.Enqueue(Accepted());

        await new GraphEmailSender(Options, handler).SendAsync("jane@example.com", "Jane Parent", "Subject", "Body");

        var tokenRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, tokenRequest.Method);
        Assert.Equal("https://login.microsoftonline.com/tenant-123/oauth2/v2.0/token", tokenRequest.Uri.ToString());
        var form = tokenRequest.Body.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("client-456", form["client_id"]);
        Assert.Equal("secret-789", form["client_secret"]);
        Assert.Equal("https://graph.microsoft.com/.default", form["scope"]);
        Assert.Equal("client_credentials", form["grant_type"]);
    }

    [Fact]
    public async Task SendAsync_PostsSendMailAsTheSharedMailbox_WithTheToken_AndDoesNotSaveToSentItems()
    {
        var handler = new ScriptedHandler();
        handler.TokenResponses.Enqueue(Token("tok-1"));
        handler.SendResponses.Enqueue(Accepted());

        await new GraphEmailSender(Options, handler).SendAsync("jane@example.com", "Jane Parent", "Set up your account", "Hello Jane");

        var send = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, send.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/noreply@varangians.co.uk/sendMail", send.Uri.ToString());
        Assert.Equal("Bearer tok-1", send.Auth);

        using var json = JsonDocument.Parse(send.Body);
        var message = json.RootElement.GetProperty("message");
        Assert.Equal("Set up your account", message.GetProperty("subject").GetString());
        Assert.Equal("Text", message.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("Hello Jane", message.GetProperty("body").GetProperty("content").GetString());
        var recipient = Assert.Single(message.GetProperty("toRecipients").EnumerateArray()).GetProperty("emailAddress");
        Assert.Equal("jane@example.com", recipient.GetProperty("address").GetString());
        Assert.Equal("Jane Parent", recipient.GetProperty("name").GetString());
        Assert.False(json.RootElement.GetProperty("saveToSentItems").GetBoolean());
    }

    [Fact]
    public async Task SendAsync_ReusesTheTokenUntilNearExpiry_ThenFetchesANewOne()
    {
        var handler = new ScriptedHandler();
        var time = new ManualTime();
        handler.TokenResponses.Enqueue(Token("tok-1", expiresIn: 3600));
        handler.TokenResponses.Enqueue(Token("tok-2", expiresIn: 3600));
        for (var i = 0; i < 3; i++) handler.SendResponses.Enqueue(Accepted());
        var sender = new GraphEmailSender(Options, handler, time);

        await sender.SendAsync("a@example.com", "A", "S", "B");
        time.Now = time.Now.AddMinutes(50);
        await sender.SendAsync("b@example.com", "B", "S", "B");
        time.Now = time.Now.AddMinutes(6); // 56 minutes in: inside the 5-minute refresh margin
        await sender.SendAsync("c@example.com", "C", "S", "B");

        var sends = handler.Requests.Where(r => r.Uri.Host == "graph.microsoft.com").Select(r => r.Auth).ToList();
        Assert.Equal(["Bearer tok-1", "Bearer tok-1", "Bearer tok-2"], sends);
        Assert.Equal(2, handler.Requests.Count(r => r.Uri.Host == "login.microsoftonline.com"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public async Task SendAsync_AnythingOtherThan202_Throws_WithTheStatus(HttpStatusCode status)
    {
        var handler = new ScriptedHandler();
        handler.TokenResponses.Enqueue(Token("tok-1"));
        handler.SendResponses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent("""{"error":{"code":"ErrorAccessDenied"}}""") });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GraphEmailSender(Options, handler).SendAsync("a@example.com", "A", "S", "B"));

        Assert.Equal(status, ex.StatusCode);
        Assert.Contains("ErrorAccessDenied", ex.Message);
    }

    [Fact]
    public async Task SendAsync_401_DropsTheCachedToken_SoTheNextSendFetchesAFreshOne()
    {
        var handler = new ScriptedHandler();
        handler.TokenResponses.Enqueue(Token("tok-1"));
        handler.TokenResponses.Enqueue(Token("tok-2"));
        handler.SendResponses.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.SendResponses.Enqueue(Accepted());
        var sender = new GraphEmailSender(Options, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync("a@example.com", "A", "S", "B"));
        await sender.SendAsync("a@example.com", "A", "S", "B");

        Assert.Equal("Bearer tok-2", handler.Requests.Last().Auth);
    }

    [Fact]
    public async Task SendAsync_TokenRequestRefused_Throws_AndSendsNothing()
    {
        var handler = new ScriptedHandler();
        handler.TokenResponses.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided."}""")
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GraphEmailSender(Options, handler).SendAsync("a@example.com", "A", "S", "B"));

        Assert.Contains("invalid_client", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "graph.microsoft.com");
    }

    [Fact]
    public void Options_BindFromTheGraphMailSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GraphMail:TenantId"] = "t",
            ["GraphMail:ClientId"] = "c",
            ["GraphMail:ClientSecret"] = "s",
            ["GraphMail:SenderAddress"] = "noreply@varangians.co.uk"
        }).Build();

        Assert.Equal(new GraphEmailOptions("t", "c", "s", "noreply@varangians.co.uk"), GraphEmailOptions.FromConfiguration(configuration));
    }

    [Theory]
    [InlineData("TenantId")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("SenderAddress")]
    public void Options_MissingValue_ThrowsNamingTheKey(string missing)
    {
        var values = new Dictionary<string, string?>
        {
            ["GraphMail:TenantId"] = "t",
            ["GraphMail:ClientId"] = "c",
            ["GraphMail:ClientSecret"] = "s",
            ["GraphMail:SenderAddress"] = "noreply@varangians.co.uk",
            [$"GraphMail:{missing}"] = " "
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => GraphEmailOptions.FromConfiguration(configuration));
        Assert.Contains($"GraphMail:{missing}", ex.Message);
    }

    [Theory]
    [InlineData("src/Vlms.Web/appsettings.json")]
    [InlineData("src/Vlms.Jobs/appsettings.json")]
    public void Appsettings_CarryTheRealIdentifiers_ButNoRealSecret(string file)
    {
        var path = Path.Combine([RepoRoot(), .. file.Split('/')]);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var graph = json.RootElement.GetProperty("GraphMail");

        Assert.Equal("9abc26d2-cda5-428d-a8ad-ccfbb0788b9a", graph.GetProperty("TenantId").GetString());
        Assert.Equal("e74439c2-2ada-4204-83b9-d0e686b8675f", graph.GetProperty("ClientId").GetString());
        Assert.Equal("noreply@varangians.co.uk", graph.GetProperty("SenderAddress").GetString());
        Assert.Equal("PLACEHOLDER", graph.GetProperty("ClientSecret").GetString());
        Assert.False(json.RootElement.TryGetProperty("Smtp", out _));
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
