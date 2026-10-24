# ADR-0011: Offline access and the native app

Status: **Accepted** (Philip, 25/10/2026). Amends `0001` (the client choice stands for the MVP; its rejection of a native app is reopened for a later stage).

## Context

Offline access was part of Philip's brief from the outset, but it was never recorded: no requirement, ADR or design document mentions it, and `0001` states the device requirement only as "Must be accessible via phone, tablet, and browser". On that basis `0001` chose a Blazor Web App installable as a PWA and rejected a .NET MAUI native app with a separate Web API for its store and release overhead. The gap surfaced on 25/10/2026.

The app as built cannot work offline. Blazor Server runs every component on the server over a live SignalR circuit, so with no connection the page stalls until it reconnects; the service worker exists only to make the app installable (`openwiki/web.md`). Adding offline use to this design is not possible: it needs code and data on the device.

## Decision

1. **The MVP goes live as the online-only PWA** (`0001`, `0005` unchanged), with the queued Play Store listing as a Trusted Web Activity.
2. **Offline access is recorded as a requirement** (`requirements/business-requirements.md`) and **met later by a .NET MAUI Blazor Hybrid app** on the roadmap (`roadmap.md`, "Later"). The Razor components move into a shared Razor class library used by both the web app and the MAUI app, the pattern of Microsoft's .NET MAUI Blazor Hybrid and Web App template ([Build a .NET MAUI Blazor Hybrid app with a Blazor Web App](https://learn.microsoft.com/aspnet/core/blazor/hybrid/tutorials/maui-blazor-web-app); [Combine Blazor Hybrid with .NET MAUI and RCLs](https://learn.microsoft.com/aspnet/core/blazor/hybrid/class-libraries-best-practices)).
3. The detailed design (API shape, sign-in, what is stored on the phone, sync and conflicts) gets its own ADR when the item is started, after the offline scope is confirmed.

## Update (Philip, 25/10/2026): scope is lesson content only

Offline access means lesson content only: a person can make lessons available offline, downloading them to the device. Nothing else needs to work offline. This removes the need for offline data and sync on the phone, and none of the safeguarding data would ever be held offline. A PWA can likely meet this scope (the service worker saves chosen lesson files and pages to the device), Decided (Philip, 25/10/2026): the PWA meets it now as an interim workaround (`requirements/functional.md` FR-023): the service worker saves the lessons a person chooses and serves them, and a saved-lessons page when there is no connection. The MAUI app (decision 2) stays on the roadmap. Decision 1 stands. Constraint on the design: many users have very poor technical skills, so the workaround must add one button per lesson and nothing to set up.

## Consequences

- Until the MAUI app exists, the app needs a connection; a teacher in a hall with no signal cannot take a register or record completions in it.
- The MAUI app is a re-architecture, not an add-on. Pages now call services that talk to PostgreSQL directly; the phone app must call a Web API instead, so every role check, EF Core query filter (`0004`, `0006`, `0010`) and read audit (`IMaterializationInterceptor`) has to hold at the API boundary, and sign-in needs tokens instead of the cookie and circuit.
- Data held on the phone is a new safeguarding exposure. Whether `DbsCheck` and `ConsentSensitiveDetails` may ever be stored offline is for the design ADR; the default is that they are not.
- Two clients to maintain and release: Android and iOS store accounts, signing and reviews, Apple Developer Program membership, and a Mac build host for iOS ([Pair to Mac for iOS development](https://learn.microsoft.com/dotnet/maui/ios/pair-to-mac)). This is the overhead `0001` rejected; it is now accepted as the price of offline access.
- The MAUI app also opens the iOS App Store, which the PWA route does not.
