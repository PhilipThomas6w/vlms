# Constraints

Status: in progress

## Technology

- Platform: .NET (confirmed, fresh implementation — not reusing the prior Power Platform/SharePoint/Dataverse/Power Automate/Asana build).
- Access: must work via phone, tablet, and browser.
- **Confirmed stack — see `adr/0001-technology-stack.md` as amended by `adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`:** Blazor Web App (PWA) + self-hosted Linux VPS (Kestrel behind Caddy) + self-hosted PostgreSQL + self-rolled ASP.NET Core Identity (TOTP MFA) + local disk file storage + SMTP email + QuestPDF. (Originally Azure App Service (Linux) + Azure SQL Database + Microsoft Entra External ID + Azure Blob Storage + Azure Communication Services Email — replaced for cost, per `adr/0005`.)

## Budget & timeline

- No hard budget ceiling or fixed go-live date. Build at a sustainable pace; optimise for low ongoing hosting cost as a general preference, not a hard cap.

## Team

- Solo developer (Philip); no other delivery resource.
