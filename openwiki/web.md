# Vlms.Web

Blazor Web App, **Server** interactivity (not Auto/WebAssembly — see [architecture.md](architecture.md)), scaffolded from `dotnet new blazor --interactivity Server --auth None --empty`.

## Current state

Only `Program.cs` has VLMS-specific content (DI wiring for `VlmsDbContext`, `ICurrentUserContext`, authorization policies, Entra sign-in — see [authentication-authorization.md](authentication-authorization.md)). `Components/Pages/` still holds the unmodified template pages (`Home.razor`, `Error.razor`, `NotFound.razor`) plus `Components/Layout/`, `Components/App.razor`, `Components/Routes.razor` — none of it is VLMS UI yet. The first real UI work is `STATE.md` Next item 1 (curriculum management / Teacher-Approver screens).

## `appsettings.json`

`AzureAd` section is placeholder-only — `PLACEHOLDER-*` values, explicit comment pointing at Key Vault for real secrets. `ConnectionStrings:VlmsDatabase` is a local/dev connection string, not real Azure SQL credentials. Do not commit real values here; this file is meant to stay safe to commit as-is.

## Before writing any interactive page against the authorization policies

Read [authentication-authorization.md](authentication-authorization.md)'s "Known gap" section first. `EntraCurrentUserContext` as currently wired via `IHttpContextAccessor` will silently deny every role check once a component runs in interactive (SignalR circuit) mode. This needs fixing (via `AuthenticationStateProvider`/persisted circuit state) as part of building the first interactive page, not discovered by debugging a mysterious "why can't the Teacher see anything" bug later.
