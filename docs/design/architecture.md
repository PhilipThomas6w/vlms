# Architecture (HLD)

Status: in progress

Per ISO/IEC/IEEE 42010:2022, this describes the system from the perspective of two stakeholders/concerns: the solo developer (buildability, low ops burden) and the safeguarding/data-protection concern (who can reach what data). Decision record: `adr/0001-technology-stack.md`, as amended by `adr/0005-hosting-pivot-self-hosted-vps-postgresql.md` (hosting, database, identity, file storage, and email all moved off Azure/Entra to a self-hosted Linux VPS — see that ADR for the cost-driven rationale).

## C4: System context

```
                    ┌─────────────────────────────────────────┐
                    │              VLMS (Blazor Web App)       │
  Teacher   ───────►│  - Lesson browsing & completion marking  │
  Approver  ───────►│  - Content proposal / approval queue     │◄─── Self-rolled ASP.NET Core
  Parent    ───────►│  - Progress dashboards, consent forms     │      Identity (sign-in, TOTP
  Student   ───────►│  - Reporting                              │      MFA, signed invite links)
  Safeg. Officer───►│  - Safeguarding/consent/DBS management     │
  Admin     ───────►│                                            │
                    └───────────────┬───────────────┬───────────┘
                                     │               │
                          ┌──────────▼───┐   ┌───────▼────────┐
                          │ PostgreSQL   │   │ Local disk      │
                          │ (EF Core /   │   │ storage         │
                          │  Npgsql)     │   │ (lesson assets, │
                          └──────────────┘   │ certificate PDFs)│
                                              └────────┬────────┘
                                                        │
                                              ┌─────────▼─────────┐
                                              │ SMTP (MailKit)      │
                                              └─────────────────────┘
```

Web app, database, and local disk storage are all co-located on one self-hosted Linux VPS (Kestrel behind Caddy for automatic HTTPS) — see `deploy/` and `openwiki/architecture.md` for the as-built shape.

## C4: Containers

- **Web app (Blazor Web App, .NET, Server interactivity, Kestrel behind Caddy on a self-hosted Linux VPS)** — the only user-facing container. Server-side rendering by default; interactive render mode where needed (e.g. live dashboards). Owns authorization (role checks) on top of self-rolled ASP.NET Core Identity authentication.
- **Database (self-hosted PostgreSQL, same VPS)** — system of record for all domain data (see `data-design.md`). Accessed only via EF Core (Npgsql) from the web app; no other component talks to it directly.
- **File storage (local disk on the VPS, `LocalDiskBlobStorage`)** — lesson content assets (documents/images referenced by a Lesson) and generated certificate PDFs, under a configurable root directory; off-box backup to a free-tier S3-compatible store (provider still to be picked — `adr/0005`). Referenced by blob key from PostgreSQL rows; access brokered through the web app (no public anonymous access, given safeguarding data proximity).
- **Background/scheduled work** — consent/DBS expiry monitoring and re-engagement checks run as `Vlms.Jobs`, a plain console app invoked daily by a systemd timer on the same VPS — decided, see `adr/0003-scheduled-jobs-webjobs.md` as amended by `adr/0005-hosting-pivot-self-hosted-vps-postgresql.md` (this was an open "WebJob vs Azure Functions Timer" question at design gate; that question is now moot).
- **Email (SMTP via MailKit)** — outbound-only; the web app and `Vlms.Jobs` call it synchronously for notification delivery. No inbound email handling in scope.
- **Identity (self-rolled ASP.NET Core Identity)** — no external tenant; the web app owns its own credential store (`VlmsIdentityUser`, sharing `AppUser`'s primary key by convention — see `data-design.md`), including built-in TOTP MFA. User roles are modelled as application-level claims (`UserRole`), not an external identity provider's groups — see `adr/0002-roles-as-application-claims.md`. Guardian onboarding uses a self-rolled signed, single-use, expiring invite link (`GuardianInvite`), replacing what was originally planned as a Microsoft Entra External ID invitation.

## Decided

- **Scheduled jobs:** a systemd timer/service pair on the self-hosted VPS invoking `Vlms.Jobs` unchanged — see `adr/0003-scheduled-jobs-webjobs.md` as amended by `adr/0005-hosting-pivot-self-hosted-vps-postgresql.md` (originally Azure App Service WebJobs).
- **Sensitive-data access control:** EF Core global query filters + read audit log, tamper-protected at the database permission level via PostgreSQL `REVOKE`/Row-Level Security — see `adr/0004-sensitive-data-access-control.md`.
- **Multi-tenancy:** none — this is a single-organisation system. Not designed for multiple Varangians-style organisations to share one deployment.
