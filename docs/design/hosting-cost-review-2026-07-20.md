# Hosting & Technology-Stack Cost Review — 2026-07-20

Status: for owner decision — this is a review producing options and a recommendation, not a decision. It does **not** change ADR-0001. Any resulting change is a separate, explicit ADR update after Philip has read this.

## Why this review exists

ADR-0001 (`docs/adr/0001-technology-stack.md`) was accepted with "no hard budget ceiling, but a stated preference for low ongoing hosting cost" as its only cost framing. That framing was never seriously interrogated against real prices. This system is built for, and its hosting bill would be paid by, a charity — discovered this session to plausibly be the Greek Orthodox Community of the 318 Holy Fathers of Nicea, Shrewsbury (`shrewsburyorthodox.com`). A recurring cloud bill in perpetuity is a real ongoing cost to that charity, not a rounding error to be waved through because "no hard ceiling" was written down once.

**Nothing is deployed to any live environment yet** (`STATE.md`): Entra External ID, Azure Blob Storage, and Azure Communication Services Email are all "structurally correct, not yet wired to a live resource" — `appsettings.json` still holds placeholder values throughout. A hosting pivot now costs relatively little in rework. This is not a sunk-cost situation.

**Mid-review update, now confirmed:** the project's Entra tenant (`shrewsburyorthodox.com`, tenant ID `9abc26d2-cda5-428d-a8ad-ccfbb0788b9a`) was provided through Microsoft's nonprofit program. This is a real, material fact and is addressed in full below (§3) — but it answers a narrower question than it might first appear to (M365 tenant provisioning, not automatically an active Azure credit grant), and one thing remains for Philip to check himself.

**The only hard constraints for this review:** (1) the documented business/functional requirements, including the safeguarding access-control/audit/retention model, must still be met; (2) the implementation is .NET. Everything else in ADR-0001 — cloud provider, hosting model, database engine, storage mechanism, identity provider, email provider — is open.

---

## 1. Executive summary (for a trustee/treasurer)

**What the design as currently written would cost, in perpetuity, at full list price:**

| Item | Monthly (list price) |
|---|---|
| Azure App Service (Linux, Basic B1) | ~$13.14 |
| Azure SQL Database (Basic tier, 5 DTU) | ~$4.90 |
| Azure SQL Database (Standard S0 tier, 10 DTU — ADR-0001 names "Basic/S0" ambiguously) | ~$14.72 |
| Azure Blob Storage (a few hundred small PDFs — negligible volume) | well under $1 |
| Azure Communication Services Email (tens of users' worth of notifications) | well under $1 |
| Microsoft Entra External ID | $0 (free under 50,000 monthly active users) |
| **Total** | **~$18–30/month, i.e. ~£170–£280/year at current rates** ([App Service Linux pricing](https://azure.microsoft.com/en-us/pricing/details/app-service/linux/), [Azure SQL Database single-database pricing](https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/), [Entra External ID pricing](https://learn.microsoft.com/en-us/entra/external-id/external-identities-pricing)) |

**What the cheapest compliant alternative costs:** a small self-managed server (a "VPS") running the same .NET application, a self-hosted database, and free-tier everything else, for **roughly $5–10/month (~£45–95/year)** — a reduction of 60–85% versus the Azure list price, achieved by removing the managed-platform premium rather than by cutting anything the safeguarding design needs.

**The one fact that could make the question moot:** this charity's Microsoft tenant was provisioned through Microsoft's nonprofit program, which separately offers a **$2,000/year Azure credit grant** ([Activate Your Azure Grant](https://learn.microsoft.com/en-us/industry/nonprofit/microsoft-for-nonprofits/claim-activate-nonprofit-azure-grant)). If that grant is active, it comfortably covers the entire Azure bill above with headroom to spare, for free. **This is not automatic** — it is a separate benefit from the Microsoft 365 tenant the charity already has, requires its own claim/activation inside the Nonprofit Hub, expires if not activated within 90 days of being issued, does not roll over year to year, and converts to a real pay-as-you-go bill if exceeded or left to lapse. Philip needs to check the Nonprofit Hub himself — see §3.

**Recommendation, plainly:** if the Azure grant is confirmed active (or the owner is willing to actively manage its annual renewal), staying on Azure is genuinely free and is the lowest-effort option — self-hosting to save a bill that's already covered by a grant would be solving a problem that doesn't exist. If the grant is not active, cannot be confirmed, or the owner does not want a system whose "free" status depends on remembering to re-claim an expiring credit every year forever, the self-hosted VPS option below is the better long-term choice for a small charity system: materially cheaper even at full price, and it removes a real dependency on an annual administrative step outside this project's control.

**Separately from cost:** identity should change regardless of which hosting path is chosen. Standing up a brand-new, dedicated Entra External ID tenant (discovered this session to be mandatory — it cannot be added to the existing workforce tenant) is disproportionate operational complexity for a solo maintainer, and it costs nothing to avoid it either way. See §5.

---

## 2. The safeguarding/compliance hard filter

Per `docs/VISION.md`, `docs/adr/0004-sensitive-data-access-control.md`, and `docs/governance/security-compliance.md`, any alternative must still deliver, unchanged:

1. **Whole-entity restriction** (not column masking) on `DbsCheck`/`ConsentSensitiveDetails`, via EF Core global query filters.
2. **A structural read-audit trail** — every read of those two entities logged via `IMaterializationInterceptor`, not an opt-in convention.
3. **Database-level tamper protection on the audit log** — `UPDATE`/`DELETE` denied at the database permission level, not just an application convention (`adr/0004` §4, `governance/raid.md` D-004).
4. **UK data-residency expectations** under UK GDPR/DPA 2018.
5. **The documented retention periods** (3 years students, 6 years DBS/audit log, hard delete).

Points 1 and 2 are **EF Core features, not database-engine features** — global query filters and `IMaterializationInterceptor` work identically whether the backing provider is SQL Server, PostgreSQL, or SQLite. They are not a live consideration in this review.

Point 3 is the one genuine technical fork in the road, and it is the most important finding in this review — see §4.3.

---

## 3. The nonprofit-credit question

**Confirmed (not in question):** the Entra tenant `shrewsburyorthodox.com` (tenant ID `9abc26d2-cda5-428d-a8ad-ccfbb0788b9a`) exists and was provisioned through Microsoft's nonprofit program.

**What that confirms, and what it doesn't:** Microsoft's nonprofit program has (at least) two separate benefit tracks, and having one does not imply the other is active:

- **Microsoft 365 nonprofit licensing** (the tenant/seat grant) — this is what's confirmed. Verified eligibility text: organisations must be "a nonprofit or non-governmental organization with recognized legal status in their respective country... operate on a not-for-profit basis and have a mission to benefit the local community" ([Microsoft Nonprofits eligibility](https://www.microsoft.com/en-us/nonprofits/eligibility)).
- **The Azure Sponsorship grant** — currently **$2,000 USD/year** (reduced from a historical $3,500/year figure found in some third-party sources) — is a **distinct offer requiring its own activation**, verified directly from Microsoft's own instructions:

  > "Once your organization is approved for Microsoft's nonprofit programs, you can access the $2,000 USD annual Azure Sponsorship grant... You must activate your Azure credit within 90 days of offer issuance. If you don't activate the credit, it might expire and become inaccessible." — [Activate Your Azure Grant, Microsoft Learn](https://learn.microsoft.com/en-us/industry/nonprofit/microsoft-for-nonprofits/claim-activate-nonprofit-azure-grant)

  Key terms confirmed from that same source, all relevant to whether this is a durable "free forever" solution or a fragile one:
  - It must be **activated by the primary contact** on the offer, inside the [Nonprofit Hub](https://nonprofit.microsoft.com) (Offers tab → "Azure $2,000 Sponsorship" → Activate) — not automatic just because the M365 tenant exists.
  - The credit is **annual and does not roll over**; it must be **renewed every year** by responding to a renewal email before expiration.
  - **A valid credit card may be required during setup**; exceeding $2,000 in a year converts the subscription to standard pay-as-you-go billing — i.e. this stops being free, silently, if usage ever exceeds the grant or the annual renewal is missed.
  - There is no process to increase the $2,000 limit.

**What I could not determine from this repository or public documentation, and what Philip needs to check himself:**
1. Whether an Azure Sponsorship offer has actually been **issued and/or activated** for this organisation (as opposed to only the M365 seats). This is only visible from inside the Nonprofit Hub / Azure portal (Billing → Payment Methods → Azure Credits tab), which I have no access to.
2. If issued but not yet activated, whether it is still inside its 90-day activation window or has already lapsed.
3. Whether this charity's specific status (a parish/community, not necessarily a UK-registered charity in the sense Microsoft's program and Companies House/Charity Commission would separately define) was the basis on which the *existing* M365 grant was approved, and whether that same basis would also qualify for the Azure grant specifically — the eligibility text for both tracks is generic ("nonprofit... with recognized legal status"), and I have no visibility into which document or registration was used to validate the org.

**This materially changes the recommendation and is flagged as such below.** If Philip confirms an active $2,000/year Azure grant with headroom above the ~$18–30/month bill, the "minimise cost" brief is already satisfied by the accepted ADR-0001 stack, and the honest recommendation is to keep it (§6, Option A) rather than take on self-hosting to solve a bill that's already covered. If he cannot confirm this, or finds it lapsed/never claimed, Option B or C below is the durable answer.

`[TBC – Philip: check the Nonprofit Hub Offers tab and the Azure portal's Payment Methods → Azure Credits tab for this tenant before deciding.]`

---

## 4. Three costed architectures

All three are .NET (Blazor Web App), all three keep the ADR-0002 role model (application-level `UserRole`, unchanged by any of this) and the ADR-0004 mechanism (EF Core query filters + `IMaterializationInterceptor`) verbatim — the only things that change are *where* the app runs, what database engine backs it, and where files/email/identity live.

### 4.1 Option A — Azure as designed (ADR-0001), nonprofit-grant funded

The accepted stack, unchanged: App Service B1 (Linux) + Azure SQL Basic/S0 + Blob Storage + Azure Communication Services Email + Entra External ID.

| | Monthly | Annual |
|---|---|---|
| List price (from §1) | ~$18–30 | ~$216–360 |
| **Net cost if the $2,000/year Azure grant is active** | **$0** | **$0** |

**Compliance fit:** full — this is the design the ADR-0004 mechanism was built against; SQL Server's `DENY ... TO public` (already implemented, migration `SupersedeVlmsAppRoleWithPublicDenyOnSensitiveDataAccessLogs`) satisfies the tamper-protection requirement exactly as documented. Azure region selection (e.g. UK South) satisfies UK data residency without qualification.

**Maintenance burden:** lowest of the three. App Service handles OS patching, TLS certificate management, and process supervision; Application Insights (already named in `adr/0003`) gives free-tier monitoring. This is real, non-marketing value for a solo maintainer, not just a sunk architectural preference.

**The catch:** entirely dependent on §3 being resolved favourably, and on the grant being *actively re-claimed every year* — a small, recurring administrative task with real failure modes (missed renewal email, credit card requirement, silent conversion to a real bill on overrun). This is a dependency on an external program's continued goodwill and Philip's own annual diligence, not a structural guarantee.

### 4.2 Option B — Self-hosted VPS + SQLite

A single small VPS running the Blazor Web App directly under Kestrel, reverse-proxied by [Caddy](https://caddyserver.com/) for free automatic HTTPS via Let's Encrypt, with SQLite as the database.

| Item | Monthly | Source |
|---|---|---|
| VPS (Hetzner CX22: 2 vCPU / 4 GB RAM / 40 GB disk) | ~€4.35 (~$4.70–5.00, FX-dependent) | [Hetzner Cloud pricing](https://www.hetzner.com/cloud/regular-performance) (post 15 June 2026 price adjustment) |
| *or* VPS (DigitalOcean Basic Droplet, 1 GB RAM, London region LON1) | $6.00 | [DigitalOcean Droplet pricing](https://www.digitalocean.com/pricing/droplets); [London region](https://www.digitalocean.com/blog/introducing-our-london-region) |
| Database (SQLite, file-based) | $0 | — |
| File storage (local disk; a few hundred small PDFs) | $0 | — |
| *or* off-box storage/backup (Cloudflare R2, first 10 GB free, egress always free) | $0 at this volume | [Cloudflare R2 pricing](https://developers.cloudflare.com/r2/pricing/) |
| Continuous DB backup ([Litestream](https://litestream.io/), replicates SQLite to S3-compatible storage) | $0 (open-source tool + free-tier storage above) | — |
| TLS/reverse proxy (Caddy) | $0 | [Caddy automatic HTTPS](https://caddyserver.com/docs/automatic-https) |
| Email (self-hosted SMTP relay via the charity's existing M365 mailbox, or a free-tier transactional API — Brevo: 300/day free, Resend: 3,000/month free) | $0 | [SMTP AUTH client submission, Microsoft Learn](https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission); [Brevo free SMTP tiers](https://www.brevo.com/blog/free-smtp-servers/) |
| Identity (self-rolled ASP.NET Core Identity, built-in TOTP MFA) | $0 | see §5 |
| **Total** | **~$5–6/month (~$60–72/year, ~£45–55/year)** | |

**Compliance fit — the one real gap.** Points 1–2 of the hard filter (query filters, read-audit interceptor) transfer unchanged: EF Core's SQLite provider supports both identically. **Point 3 does not transfer.** SQLite has no user/permission model at all — the entire database is a single file; any process account with filesystem write access to that file has unrestricted read/write/delete over every table in it, including the audit log. There is no SQL-level `DENY`/`REVOKE` equivalent to port. As designed today, **SQLite cannot satisfy the "database-level tamper protection, not an application convention" requirement** (`adr/0004` §4) without a compensating control — for example, streaming each audit row to an external append-only store the app process itself cannot mutate (e.g. `Litestream`'s own replica, or a separate write-once destination), or OS-level immutability flags on the file. None of that exists today and would need to be designed, not assumed. This is a genuine, named gap, not a nitpick — flag it as such if this option is chosen.

**UK data residency:** Hetzner has no UK region (Germany/Finland/US/Singapore — [Hetzner data centre locations](https://docs.hetzner.com/cloud/general/locations/)); DigitalOcean's London region (LON1) is a literal UK option. Hosting in Germany/Finland is legally unproblematic regardless — the UK has adequacy regulations covering EEA states, so personal data can flow freely without additional safeguards ([ICO: Adequacy regulations](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/international-transfers/adequacy-regulations/)) — but DigitalOcean LON1 removes the question entirely, which may matter more for trustee-level optics than for the legal position itself.

**Maintenance burden — the honest cost.** This is not free in owner time. Philip would own: OS security patching (`unattended-upgrades` largely automates this), the systemd unit(s) for the app and the scheduled job (replacing the WebJob), verifying Litestream backups actually restore, and being the sole point of failure if anything breaks — RAID R-002's bus-factor risk, already named, is unchanged by hosting choice but has no Azure-support safety net under it here. Initial setup (Caddy + systemd + SQLite/Litestream wiring) is realistically a one-off day's work for an engineer of Philip's stated fluency; ongoing burden thereafter is low (well under an hour a month) but not zero, and there is no managed platform absorbing the difference the way App Service does.

### 4.3 Option C — Self-hosted VPS + self-hosted PostgreSQL

Identical to Option B in every respect (same VPS, same Caddy, same storage/backup/email/identity choices), with **PostgreSQL running on the same VPS** instead of SQLite.

| Item | Monthly |
|---|---|
| Same as Option B, minus SQLite, plus self-hosted PostgreSQL (Debian/Ubuntu package, $0) | Same total: **~$5–6/month** (a slightly larger VPS for comfortable headroom — e.g. Hetzner CX22's 4 GB is already generous for Postgres + a Blazor Server app at tens-of-users scale — costs the same or a few dollars more) |

**Compliance fit — this is the actual recommended default for the hard filter, at zero extra cost.** PostgreSQL has a real permission model directly analogous to SQL Server's: `REVOKE UPDATE, DELETE ON sensitive_data_access_log FROM PUBLIC` denies mutation to every role by default, the same structural guarantee `adr/0004` §4 already built for SQL Server, ported almost line-for-line rather than redesigned ([PostgreSQL `REVOKE` documentation](https://www.postgresql.org/docs/current/sql-revoke.html)). This closes the exact gap Option B leaves open, for no additional cost — there is no scenario in which SQLite is cheaper than self-hosted Postgres on the same box, so there is no cost trade-off to make here, only a small amount of extra setup (installing and configuring Postgres instead of shipping a single file) against materially better fit for a documented hard constraint.

**Everything else** (UK residency, retention, maintenance burden) is identical to Option B — Postgres adds negligible extra ongoing administration over SQLite at this scale (tens of users, no meaningful concurrent-write load: SQLite's WAL mode already comfortably handles far more than this system will ever see — "in most cases, a write transaction only takes milliseconds and so multiple writers can simply take turns" ([SQLite Write-Ahead Logging](https://sqlite.org/wal.html)) — so Postgres is not being chosen here to solve a concurrency problem this system has; it's chosen because it, unlike SQLite, actually has the permission primitive the audit-log hard constraint asks for).

A managed Postgres add-on (e.g. a $15/month DigitalOcean Managed Database) was considered and is not recommended — it triples the option's cost for automated backups/failover that Litestream-equivalent tooling (`pg_dump` on a cron, shipped to R2/B2) already provides for free at this data volume, and is not justified at tens-of-users scale.

---

## 5. Identity: cross-cutting, not a hosting decision

This applies to all three options above.

**Discovered this session and verified:** Entra External ID for customer/consumer sign-in requires a **brand-new, dedicated external tenant** — it is architecturally distinct from and cannot be added to the org's existing workforce tenant:

> "When you want to use External ID to add customer identity and access management (CIAM) to your apps, you create a new tenant in an external configuration. This tenant is distinct and separate from your workforce tenant." — [Tenant Configurations, Microsoft Entra External ID](https://learn.microsoft.com/en-us/entra/external-id/tenant-configurations)

ADR-0001 justified Entra External ID purely on "avoids building password/MFA/recovery flows solo" — never on cost, correctly, since it's free either way under 50,000 MAU ([Entra External ID pricing](https://learn.microsoft.com/en-us/entra/external-id/external-identities-pricing)). But that justification didn't account for the operational cost actually discovered: administering a second, separate Entra tenant (its own app registrations, its own guest/invite model, its own admin surface) alongside the org's existing workforce tenant, for a solo maintainer, at tens-of-users scale.

**ASP.NET Core Identity has built-in TOTP-based MFA out of the box** — no external service, no separate tenant, free: "MFA using TOTP is supported by default when using ASP.NET Core Identity... Scaffolded ASP.NET Core Identity comes with TOTP support out of the box" ([Multifactor authentication in ASP.NET Core, Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/mfa?view=aspnetcore-10.0)). Password reset, account recovery, and email-based invite flows are all standard, well-documented Identity patterns — not a from-scratch build of the kind ADR-0001 was originally worried about avoiding.

**Recommendation:** replace Entra External ID with self-rolled ASP.NET Core Identity + TOTP MFA, regardless of which hosting option is chosen. This is a complexity call, not a cost call — both options are free at this scale — and it has a genuinely useful side effect: it removes the dependency this project is currently *blocked* on (`STATE.md` "Blocked / needs decision": the Microsoft Graph guardian-invitation API, which needs a live external tenant that doesn't exist). A self-rolled Identity invite flow (an emailed link with a signed token scoped to the correct `Student` record) achieves the same "Admin/Teacher issues an invite already scoped to the right student" requirement (`design/data-design.md` "Guardian link verification") without any tenant or Graph dependency at all.

ADR-0002 (roles as application claims, not Entra groups) is unaffected either way — it already models roles in an application-owned `UserRole` table, which is exactly as compatible with ASP.NET Core Identity's `ClaimsPrincipal` as it is with Entra's.

---

## 6. Comparison and ranked recommendation

| | Option A: Azure (grant-funded) | Option A: Azure (list price) | Option B: VPS + SQLite | Option C: VPS + Postgres |
|---|---|---|---|---|
| Monthly cost | $0 | ~$18–30 | ~$5–6 | ~$5–6 |
| Annual cost | $0 | ~$216–360 | ~$60–72 | ~$60–72 |
| Tamper-protected audit log | Yes (built) | Yes (built) | **No — needs a compensating control** | Yes (direct port, `REVOKE`) |
| UK/EEA data residency | Yes (choose UK region) | Yes | Depends on provider/region chosen (DO LON1 = literal UK) | Same as B |
| Ops burden | Lowest (managed) | Lowest (managed) | Low-moderate, owner-carried | Low-moderate, owner-carried |
| External dependency risk | High — annual grant re-claim, expiry, overage-to-PAYG | None (but expensive) | None | None |

**Ranked recommendation:**

1. **If Philip confirms the $2,000/year Azure Sponsorship grant is active (or activates it) with room above the ~$18–30/month bill:** keep Option A. It is genuinely free, lowest-effort, and the ADR-0004 mechanism is already built and tested against it. Do this only after resolving §3 — don't assume it.
2. **If the grant cannot be confirmed, isn't active, or the owner doesn't want the system's "free" status to depend on an annual administrative renewal outside this project's control:** move to **Option C (self-hosted VPS + PostgreSQL)**. It costs the same as Option B, closes Option B's one real compliance gap at zero extra cost, and cuts the *list-price* cost of the current design by roughly 70–85% for a maintenance burden that is real but modest for an engineer of Philip's stated background.
3. **Option B (SQLite) is not recommended over Option C** at the prices found — there is no cost reason to accept its weaker tamper-protection story when Postgres is free on the same box.
4. **Identity: move off Entra External ID to self-rolled ASP.NET Core Identity regardless of 1–3.** It's free either way, removes a live architectural blocker, and is proportionate to a solo-maintained, tens-of-users system.

---

## 7. Open items for the owner

- `[TBC – Philip]`: check the Nonprofit Hub (`nonprofit.microsoft.com` → Offers) and the Azure portal (Billing → Payment Methods → Azure Credits) for this tenant to confirm whether the $2,000/year Azure Sponsorship grant has been issued, and if so, whether it's been activated and is still within its window. This is the single fact that most changes the ranking above.
- `[TBC – Philip]`: confirm the organisation's registration basis (parish/charity status) that was used for the existing M365 nonprofit approval, and whether that basis is documented anywhere accessible for a future Azure-grant application if one hasn't been made.
- Not addressed here (unchanged by this review either way): the DPIA screening (`raid.md` D-003) and verified email sending domain (`raid.md` D-002) remain owed before go-live regardless of which hosting option is chosen.

---

## Sources

- [App Service pricing (Linux)](https://azure.microsoft.com/en-us/pricing/details/app-service/linux/)
- [Azure SQL Database single-database pricing](https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/)
- [Azure Blob Storage pricing](https://azure.microsoft.com/en-us/pricing/details/storage/blobs/)
- [Azure Communication Services Email pricing](https://learn.microsoft.com/en-us/azure/communication-services/concepts/email-pricing)
- [Microsoft Entra External ID pricing](https://learn.microsoft.com/en-us/entra/external-id/external-identities-pricing)
- [Microsoft Nonprofits eligibility](https://www.microsoft.com/en-us/nonprofits/eligibility)
- [Activate Your Azure Grant, Microsoft for Nonprofits](https://learn.microsoft.com/en-us/industry/nonprofit/microsoft-for-nonprofits/claim-activate-nonprofit-azure-grant)
- [Hetzner Cloud pricing](https://www.hetzner.com/cloud/regular-performance); [Hetzner data centre locations](https://docs.hetzner.com/cloud/general/locations/)
- [DigitalOcean Droplet pricing](https://www.digitalocean.com/pricing/droplets); [DigitalOcean London region (LON1)](https://www.digitalocean.com/blog/introducing-our-london-region)
- [Cloudflare R2 pricing](https://developers.cloudflare.com/r2/pricing/)
- [Backblaze B2 pricing](https://www.backblaze.com/cloud-storage/pricing)
- [Litestream — streaming SQLite replication](https://litestream.io/); [SQLite Write-Ahead Logging](https://sqlite.org/wal.html)
- [Caddy — Automatic HTTPS](https://caddyserver.com/docs/automatic-https)
- [SMTP AUTH client submission in Exchange Online, Microsoft Learn](https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission)
- [Brevo free SMTP tiers comparison](https://www.brevo.com/blog/free-smtp-servers/)
- [Microsoft Entra External ID tenant configurations](https://learn.microsoft.com/en-us/entra/external-id/tenant-configurations)
- [Multifactor authentication in ASP.NET Core, Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/mfa?view=aspnetcore-10.0)
- [PostgreSQL `REVOKE` documentation](https://www.postgresql.org/docs/current/sql-revoke.html)
- [SQL Server `DENY (Transact-SQL)`, Microsoft Learn](https://learn.microsoft.com/en-us/sql/t-sql/statements/deny-transact-sql) (the mechanism `adr/0004` §4 already implements for SQL Server, referenced for comparison)
- [ICO: Adequacy regulations](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/international-transfers/adequacy-regulations/)
