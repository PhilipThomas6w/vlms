# Roadmap

Status: initial (Philip, 25/10/2026). The roadmap orders the work by stage; `STATE.md` holds the live queue and detail, and `delivery-plan.md` the phases. No dates: solo delivery, no hard deadline (`requirements/constraints.md`).

## Now: MVP to go-live (PWA)

The app ships as a Blazor Server Progressive Web App, installed from the browser or the Play Store, online only (`adr/0001`, `adr/0011`).

- Open decisions in `STATE.md` "Decisions needed": retention purge approach, alert route, ops pack markers, palette and icon, hosting go-ahead, certificate designs.
- **Lessons saved for offline use** (FR-023; Philip, 25/10/2026): interim offline access in the PWA, one button per lesson.
- Retention purge; branding and WCAG colour contrast.
- Human security and accessibility review.
- VPS go-live at www.varangians.co.uk, then the backup restore test.
- UAT, then re-render the delivery gate package.

## Next: after go-live

- **Play Store listing** (Trusted Web Activity wrapping the PWA; Philip, 12/10/2026). Needs go-live and branding.
- **Lesson content as in-app HTML** (Philip, 21/10/2026): one printable page per lesson, viewed without downloading.

## Later

- **Offline-capable phone app: .NET MAUI Blazor Hybrid** (Philip, 25/10/2026; `adr/0011`). Replaces the interim PWA workaround (FR-023) as the long-term way to meet the offline access requirement (`requirements/business-requirements.md`), with lessons held by the app itself and an iOS App Store listing. A native shell for Android and iOS that reuses the existing Razor components through a shared Razor class library. The big part of the job is server-side: a Web API with token sign-in, and the access-control and audit rules re-applied at that API. Offline scope is lesson content only (Philip, 25/10/2026), so no sync of other data. It also needs Apple Developer Program membership, a Mac build host for iOS, and store releases.
- **Merchandise shop** (Philip, 24/10/2026), once the brand design is settled; payment through a hosted checkout.
- **At-home learning beyond lesson access**, such as assignments set between sessions.
