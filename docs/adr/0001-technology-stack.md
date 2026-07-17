# 0001 - Technology stack: Blazor + Azure

## Status

Accepted

## Context

VLMS is a fresh .NET rebuild of a previously abandoned Power Platform-based implementation (SharePoint, Power Apps, Dataverse, Power Automate, Asana). The prior tooling is deliberately not being reused. Requirements (see `docs/requirements/`):

- Must be accessible via phone, tablet, and browser.
- Solo developer, no dedicated ops team, must minimise ongoing maintenance burden.
- Small scale: tens of users per role (Admin, Teacher, Approver, Parent/Guardian, Student, Safeguarding Officer), not hundreds/thousands.
- No hard budget ceiling, but a stated preference for low ongoing hosting cost.
- Manages special-category/children's personal data (safeguarding, medical, consent, DBS) — see `governance/security-compliance.md`.
- Standalone system: no integration with an existing membership/payments/calendar platform; only needs outbound transactional email.
- Certificates are auto-generated PDF documents.

## Decision

Adopt the following stack:

| Concern | Choice |
|---|---|
| Client | Blazor Web App (.NET), responsive, PWA-installable |
| Hosting | Azure App Service (Linux), Basic (B1) tier initially |
| Database | Azure SQL Database, Basic/S0 tier initially |
| Identity | Microsoft Entra External ID (CIAM) for Teacher/Approver/Parent/Student/Safeguarding Officer/Admin sign-in |
| File storage | Azure Blob Storage — lesson content assets, generated certificate PDFs |
| Email | Azure Communication Services Email — transactional email (notifications, expiry reminders) |
| PDF generation | QuestPDF (open-source .NET library, Community licence) |

## Alternatives considered

- **Client:** .NET MAUI native app + separate Web API — rejected: native app-store distribution and update cycles add solo-maintainer overhead disproportionate to a tens-of-users audience; Blazor meets the phone/tablet/browser requirement with one codebase.
- **Identity:** Azure AD B2C — rejected: retired for new customers as of May 2025 ([Microsoft Entra fundamentals](https://learn.microsoft.com/entra/architecture/secure-fundamentals#microsoft-entra-functional-areas)); Entra External ID is Microsoft's current CIAM product ([Introduction to Microsoft Entra External ID](https://learn.microsoft.com/entra/external-id/external-identities-overview)). Custom ASP.NET Core Identity — rejected: would require building and maintaining password/MFA/account-recovery flows solo, which Entra External ID provides out of the box.
- **Hosting:** Azure Container Apps / Functions — rejected for v1: App Service is the simplest operational model for a single Blazor Web App at this scale; can revisit if scale-to-zero cost savings become material.
- **Database:** Azure Cosmos DB — rejected: the domain is strongly relational (Rank → Lesson → Completion, Student ↔ Parent, Consent approval workflow) and best served by a relational store with EF Core.
- **PDF generation:** IronPDF, DinkToPdf — QuestPDF chosen for its free Community licence at this scale and native C# fluent API (no HTML/headless-browser dependency).

## Consequences

- All identity/auth work is delegated to Entra External ID; user-role mapping (Admin/Teacher/Approver/Parent/Student/Safeguarding Officer) must be modelled as application-level authorization (e.g. custom claims or app roles), not built from scratch.
- QuestPDF is a non-Microsoft dependency; licence terms should be re-checked if the project ever exceeds the Community licence's revenue/org-size threshold.
- Azure Communication Services Email requires a verified sending domain before go-live.
- Starting tiers (App Service B1, SQL Basic/S0) are sized for tens-of-users load; scaling up is a configuration change, not a redesign, if usage grows materially beyond the ~2-year assumption in `governance/raid.md` (A-002).
