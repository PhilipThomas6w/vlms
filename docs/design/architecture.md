# Architecture (HLD)

Status: in progress

Per ISO/IEC/IEEE 42010:2022, this describes the system from the perspective of two stakeholders/concerns: the solo developer (buildability, low ops burden) and the safeguarding/data-protection concern (who can reach what data). Decision record: `adr/0001-technology-stack.md`.

## C4: System context

```
                    ┌─────────────────────────────────────────┐
                    │              VLMS (Blazor Web App)       │
  Teacher   ───────►│  - Lesson browsing & completion marking  │
  Approver  ───────►│  - Content proposal / approval queue     │◄─── Microsoft Entra External ID
  Parent    ───────►│  - Progress dashboards, consent forms     │      (sign-in, MFA, self-service)
  Student   ───────►│  - Reporting                              │
  Safeg. Officer───►│  - Safeguarding/consent/DBS management     │
  Admin     ───────►│                                            │
                    └───────────────┬───────────────┬───────────┘
                                     │               │
                          ┌──────────▼───┐   ┌───────▼────────┐
                          │ Azure SQL DB │   │ Azure Blob      │
                          │ (EF Core)    │   │ Storage         │
                          └──────────────┘   │ (lesson assets, │
                                              │ certificate PDFs)│
                                              └────────┬────────┘
                                                        │
                                              ┌─────────▼─────────┐
                                              │ Azure Communication │
                                              │ Services (Email)    │
                                              └─────────────────────┘
```

## C4: Containers

- **Web app (Blazor Web App, .NET, hosted on Azure App Service)** — the only user-facing container. Server-side rendering by default; interactive render mode where needed (e.g. live dashboards). Owns authorization (role checks) on top of Entra External ID authentication.
- **Database (Azure SQL Database)** — system of record for all domain data (see `data-design.md`). Accessed only via EF Core from the web app; no other component talks to it directly.
- **Blob storage (Azure Blob Storage)** — lesson content assets (documents/images referenced by a Lesson) and generated certificate PDFs. Referenced by URL/blob key from SQL rows; access brokered through the web app (no public anonymous containers, given safeguarding data proximity).
- **Background/scheduled work** — consent/DBS expiry monitoring and re-engagement checks run as a scheduled job. [TBC design: Azure App Service WebJob vs a separate Azure Functions Timer trigger — see open question below.]
- **Email (Azure Communication Services Email)** — outbound-only; the web app calls it synchronously or via a lightweight queue for notification delivery. No inbound email handling in scope.
- **Identity (Microsoft Entra External ID)** — external tenant; the web app registers as a relying application (OIDC). User roles are modelled as application-level claims, not native Entra External ID groups — see `adr/0002-roles-as-application-claims.md`.

## Decided

- **Scheduled jobs:** Azure App Service WebJobs — see `adr/0003-scheduled-jobs-webjobs.md`.
- **Sensitive-data access control:** EF Core global query filters + read audit log — see `adr/0004-sensitive-data-access-control.md`.
- **Multi-tenancy:** none — this is a single-organisation system. Not designed for multiple Varangians-style organisations to share one deployment.
