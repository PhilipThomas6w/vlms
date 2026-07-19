# Notifications: `INotificationService` + `ConsentExpiryNotifier`

Design source: `docs/design/low-level-design.md` "NotificationService" (including its "Failure handling (design-review addition)" paragraph), `docs/adr/0001-technology-stack.md` (Azure Communication Services Email), `docs/quality/test-plan.md` TC-013, STATE.md.

## `INotificationService` (`src/Vlms.Domain/INotificationService.cs`)

Same placement pattern as `IBlobStorage`/`ICurrentUserContext`: the interface (plus `NotificationRequest`/`NotificationPriority`/`NotificationOutcome`) lives in Vlms.Domain so it stays technology-agnostic; the real implementation is in Vlms.Infrastructure.

- **`NotificationPriority`** — `Standard` or `SafeguardingCritical`. Drives the real implementation's retry/escalation branching, matching low-level-design.md's own two-tier wording exactly.
- **`NotificationRequest`** — recipient email/name, subject, plain-text body, priority. No templating engine — content is caller-built, since nothing in the design docs asks for one at this scale.
- **`NotificationOutcome`** — `Sent` / `SentAfterRetry` / `FailedLoggedOnly` / `FailedAndEscalated`. `SendAsync` never throws; a failure (even after every retry) is reported via this enum so a caller sending to several recipients can keep going past one failure.

## Real implementation (`src/Vlms.Infrastructure/Notifications/`)

- **`IEmailSender`** — the raw "send one email, throw on failure" seam. Infrastructure-only (nothing in Vlms.Domain/Vlms.Web needs it) — exists purely so `NotificationService`'s retry logic can be tested against a fake (`FakeEmailSender` in tests) without mocking Azure Communication Services' sealed `EmailClient`.
- **`AzureCommunicationEmailSender`** — the real `IEmailSender`, wraps `Azure.Communication.Email.EmailClient.SendAsync(WaitUntil.Completed, senderAddress, recipientAddress, subject, htmlContent, plainTextContent, cancellationToken)`. API shape verified against Microsoft Learn (not guessed, per CLAUDE.md). Only `EmailSendStatus.Succeeded` counts as success; anything else throws, which `NotificationService`'s retry loop treats like any other transport failure. Takes an already-constructed `EmailClient` (built from a connection string by the caller, `Vlms.Jobs/Program.cs`) — same "caller builds the SDK client, this class just uses it" shape as `Storage/AzureBlobStorage.cs` taking a `BlobServiceClient`.
- **`NotificationService`** — the retry/escalation logic itself:
  - `SafeguardingCritical`: up to `DefaultSafeguardingCriticalMaxAttempts` (3, a documented build-time default — the design doc says "with backoff" but names no figure, the same "configurable, no number given" situation `ConsentExpiryJob.DefaultExpiryWarningWindowDays` already resolved) attempts, with a backoff delay between them. Every attempt failing logs at `LogError` ("ESCALATION: ...") and returns `FailedAndEscalated`.
  - `Standard`: exactly one attempt; failure logs at `LogWarning` and returns `FailedLoggedOnly` — no escalation, matching the design doc's "log failure without escalation" wording for non-critical notifications.
  - The backoff `delay` function is a constructor parameter (defaulting to a real `Task.Delay`) purely so tests don't have to wait through real backoff — same reason `ConsentExpiryJob` takes `expiryWarningWindowDays` as a constructor parameter rather than a hardcoded value.

## `ConsentExpiryNotifier` (`src/Vlms.Infrastructure/Safeguarding/ConsentExpiryNotifier.cs`)

Translates a `ConsentExpiryJob` sweep's `ConsentExpiryJobResult` into real safeguarding-critical emails — the mechanism `ConsentExpiryJob`'s own doc comment named as the reason "escalation" was log-only until now.

Three scope decisions, documented in the class itself:

1. **Alongside, not instead of** — `ConsentExpiryJob.LogFlags` keeps writing its structured log entries unchanged (adr/0003 names Application Insights alerting on WebJob logs as the whole job's monitoring mechanism, not just this one notification path); `ConsentExpiryNotifier` is purely additive, called by `Vlms.Jobs/Program.cs` straight after `ConsentExpiryJob.RunAsync()` returns.
2. **Only the two `IsExpired = true` flag categories are "safeguarding-critical"** — no valid consent, no valid DBS — matching low-level-design.md's own wording exactly ("a failed send for a safeguarding-critical notification (expired consent, expired DBS) is retried with backoff..."). Approaching-window consent/DBS flags and at-risk/disengaged students stay log-only in this increment (`ConsentExpiryJob.LogFlags` already covers them at `LogWarning`) — STATE.md's item wording is specifically "retry/escalation for safeguarding-critical notifications", and a routine/non-critical email path (cadence, recipient — Admin, or the affected parent/teacher themselves?) is out of scope here. `NotificationService`'s `Standard`-priority path is fully built and tested regardless, ready for a future increment that wires up routine parent-facing emails (completion/promotion confirmations, functional.md "Parent engagement").
3. **One digest email per Admin/SafeguardingOfficer `AppUser`, not one email per flagged item per recipient** — a daily sweep with N flags and M recipients sending N × M separate emails would be unusable noise; a single digest matches the "daily sweep" framing and "escalates to Admin/Safeguarding Officer" wording (the escalation is to the role, as a whole).

Role-checked inside `NotifyAsync` itself (defense in depth, same pattern as `ConsentExpiryJob`'s own `RequireAdminOrSafeguardingOfficer`), even though in production the caller is always `SystemCurrentUserContext`.

## `Vlms.Jobs/Program.cs` wiring

`EmailClient` is constructed from `CommunicationServices:ConnectionString` (appsettings.json — **placeholder-only**, same status `AzureAd` had before a live Entra tenant existed: no live Azure Communication Services resource exists yet). `AzureCommunicationEmailSender`/`NotificationService`/`ConsentExpiryNotifier` are registered, and `ConsentExpiryNotifier.NotifyAsync(result)` is called immediately after `ConsentExpiryJob.RunAsync()` returns, inside the same try/catch that already handles the job's own failures.

## Tests

`tests/Vlms.Tests/Infrastructure/NotificationServiceTests.cs` (retry/escalation logic, using `FakeEmailSender` + a no-op delay function so backoff doesn't make tests wait), `ConsentExpiryNotifierTests.cs` (recipient resolution/digest content, SQLite-in-memory-via-DI pattern, using `FakeNotificationService` to record what was sent without needing a real `IEmailSender`). `FakeEmailSender`/`FakeNotificationService` are new hand-rolled test doubles, same spirit as `InMemoryBlobStorage`/`ListLogger` — no mocking library.
