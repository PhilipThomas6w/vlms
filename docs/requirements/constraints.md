# Constraints

Status: in progress

## Technology

- Platform: .NET (confirmed, fresh implementation — not reusing the prior Power Platform/SharePoint/Dataverse/Power Automate/Asana build).
- Access: must work via phone, tablet, and browser.
- **Confirmed stack — see `adr/0001-technology-stack.md`:** Blazor Web App (PWA) + Azure App Service (Linux) + Azure SQL Database + Microsoft Entra External ID + Azure Blob Storage + Azure Communication Services Email + QuestPDF.

## Budget & timeline

- No hard budget ceiling or fixed go-live date. Build at a sustainable pace; optimise for low ongoing hosting cost as a general preference, not a hard cap.

## Team

- Solo developer (Philip); no other delivery resource.
