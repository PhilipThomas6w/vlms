# Constraints

Status: in progress

## Technology

- Platform: .NET (confirmed, fresh implementation — not reusing the prior Power Platform/SharePoint/Dataverse/Power Automate/Asana build).
- Access: must work via phone, tablet, and browser.
- **Working recommendation (not yet a confirmed ADR):** Blazor Web App (responsive, PWA-installable) as client, hosted on Azure App Service (Linux, Basic tier to start) + Azure SQL Database + Azure Blob Storage + Microsoft Entra External ID for parent/teacher/student auth. To be confirmed as `ADR-0001` at design time. Sources: [Blazor supported platforms](https://learn.microsoft.com/aspnet/core/blazor/supported-platforms), [Blazor PWA](https://learn.microsoft.com/aspnet/core/blazor/progressive-web-app/), [Azure App Service plans/pricing](https://learn.microsoft.com/azure/app-service/overview-hosting-plans).

## Budget & timeline

- No hard budget ceiling or fixed go-live date. Build at a sustainable pace; optimise for low ongoing hosting cost as a general preference, not a hard cap.

## Team

- Solo developer (Philip); no other delivery resource.
