using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Vlms.Infrastructure.Notifications;

/// <summary>
/// Settings for <see cref="GraphEmailSender"/>, bound from the <c>GraphMail</c> configuration
/// section. Tenant ID, client ID and sender address are identifiers, not secrets, and live in
/// appsettings.json. <c>GraphMail:ClientSecret</c> is a secret: set only via the server's
/// environment files (<c>GraphMail__ClientSecret</c>, deploy/README.md), never committed.
/// </summary>
public sealed record GraphEmailOptions(string TenantId, string ClientId, string ClientSecret, string SenderAddress)
{
    public const string Section = "GraphMail";

    public static GraphEmailOptions FromConfiguration(IConfiguration configuration) => new(
        Required(configuration, "TenantId"),
        Required(configuration, "ClientId"),
        Required(configuration, "ClientSecret"),
        Required(configuration, "SenderAddress"));

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[$"{Section}:{key}"];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{Section}:{key} is not configured.")
            : value;
    }
}

/// <summary>
/// The real <see cref="IEmailSender"/>: sends through Microsoft Graph <c>sendMail</c> as the
/// shared mailbox <c>noreply@varangians.co.uk</c> (adr/0005 amendment, 12/10/2026), replacing
/// SMTP with a username and password, which Microsoft 365 is retiring.
///
/// <b>Authentication:</b> app-only OAuth 2.0 client credentials for the "Varangians LMS mailer"
/// Entra app. The app has no tenant-wide Graph permission; Exchange RBAC for Applications grants it
/// <c>Mail.Send</c> on that one mailbox only, so a token can't be used to send as anyone else.
/// Tokens are cached until five minutes before expiry, and dropped on a 401 so the next attempt
/// fetches a fresh one.
///
/// <b>Failures throw</b> (token request refused, any status other than 202 from <c>sendMail</c>),
/// so <see cref="NotificationService"/>'s retry and escalation see them. A 202 means Exchange
/// accepted the message, not that it was delivered (Microsoft Learn, user: sendMail).
///
/// <b>Sent Items:</b> <c>saveToSentItems</c> is false. Messages carry password-reset, confirmation
/// and invite links, which shouldn't accumulate in a mailbox other people can open.
///
/// One long-lived <see cref="HttpClient"/> per instance (registered as a singleton), with pooled
/// connections recycled every 15 minutes so DNS changes are picked up.
/// </summary>
public sealed class GraphEmailSender : IEmailSender
{
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    private readonly GraphEmailOptions _options;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;

    public GraphEmailSender(GraphEmailOptions options, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _options = options;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) });
        _time = time ?? TimeProvider.System;
    }

    public async Task SendAsync(string recipientEmail, string recipientName, string subject, string body, CancellationToken ct = default)
    {
        var token = await GetTokenAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GraphBaseUrl}/users/{_options.SenderAddress}/sendMail")
        {
            Content = JsonContent.Create(new SendMailRequest(
                new Message(
                    subject,
                    new ItemBody("Text", body),
                    [new Recipient(new EmailAddress(recipientEmail, recipientName))]),
                SaveToSentItems: false))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _accessToken = null;
        }

        throw new HttpRequestException(
            $"Microsoft Graph sendMail returned {(int)response.StatusCode} {response.StatusCode}: {await ReadSnippetAsync(response, ct)}",
            null,
            response.StatusCode);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && _time.GetUtcNow() < _tokenExpiresAt - TokenRefreshMargin)
            {
                return _accessToken;
            }

            using var response = await _http.PostAsync(
                $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret,
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["grant_type"] = "client_credentials"
                }),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Microsoft Entra token request returned {(int)response.StatusCode} {response.StatusCode}: {await ReadSnippetAsync(response, ct)}",
                    null,
                    response.StatusCode);
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                ?? throw new InvalidOperationException("Microsoft Entra token response was empty.");
            if (string.IsNullOrEmpty(token.AccessToken))
            {
                throw new InvalidOperationException("Microsoft Entra token response had no access_token.");
            }

            _accessToken = token.AccessToken;
            _tokenExpiresAt = _time.GetUtcNow().AddSeconds(token.ExpiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // Error bodies are JSON error descriptions (no secrets); capped so a large body can't flood the log.
    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length <= 500 ? text : text[..500];
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record SendMailRequest(
        [property: JsonPropertyName("message")] Message Message,
        [property: JsonPropertyName("saveToSentItems")] bool SaveToSentItems);

    private sealed record Message(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("body")] ItemBody Body,
        [property: JsonPropertyName("toRecipients")] Recipient[] ToRecipients);

    private sealed record ItemBody(
        [property: JsonPropertyName("contentType")] string ContentType,
        [property: JsonPropertyName("content")] string Content);

    private sealed record Recipient([property: JsonPropertyName("emailAddress")] EmailAddress EmailAddress);

    private sealed record EmailAddress(
        [property: JsonPropertyName("address")] string Address,
        [property: JsonPropertyName("name")] string Name);
}
