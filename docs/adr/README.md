# Architecture Decision Records

Status: in progress — 11 ADRs recorded (see below).

ADRs are added here as `NNNN-<slug>.md` in Nygard format (Context, Decision, Status, Consequences) as decisions are made during design.

- `0001-technology-stack.md` — Blazor + Azure stack.
- `0002-roles-as-application-claims.md` — user roles as application claims, not native Entra External ID groups.
- `0003-scheduled-jobs-webjobs.md` — background/scheduled work via Azure App Service WebJobs.
- `0004-sensitive-data-access-control.md` — `ConsentRecord`/`ConsentSensitiveDetails` entity split + EF Core global query filters + `IMaterializationInterceptor`-based read audit log for `DbsCheck`/`ConsentSensitiveDetails`.
- `0005-hosting-pivot-self-hosted-vps-postgresql.md` — self-hosted Linux VPS + PostgreSQL + ASP.NET Core Identity + local disk storage; partially supersedes 0001, amends 0003.
- `0006-parent-consent-and-teacher-read-access.md` — parents submit the annual consent form; `ConsentSensitiveDetails` also readable by Teachers and by the student's linked Parents (amends 0004; `DbsCheck` unchanged).
- `0007-student-sign-in-accounts.md` — parent-created student sign-ins (username and password, no email); `VlmsUserConfirmation` keeps the confirmed-email rule for adults and admits an emailless account only when it is a student's sign-in (FR-011).
- `0008-parent-registration-and-annual-consent-year.md` — parents apply to register their children, with consent in the same flow; an Admin approves and sets the starting category; consent ends 31 August; separate transport/off-site consents and dietary/SEN fields (amends FR-001, FR-003, FR-004).
- `0009-browser-wcag-checks-in-the-gate.md` — Playwright + axe-core browser tests in the full gate (colour contrast, reflow); reverses the rationale recorded in `build/check-accessibility.ps1`; lists what stays manual and the baseline of known violations.
- `0010-chapters.md` — chapters (FR-021): every person-level record carries its chapter, separated by a named EF Core query filter alongside the sensitive-data filters; roles per chapter with an active chapter per person; a National Administrator with no access to any chapter's children (amends 0002, 0004, 0006, 0008).
- `0011-offline-access-and-native-app.md` — offline access is a requirement the online-only PWA cannot meet; the MVP goes live as the PWA, and a .NET MAUI Blazor Hybrid app sharing the Razor components is on the roadmap (amends 0001).
- `0012-app-hosted-lessons.md` — lessons become app-hosted text and pictures (Markdown, converted once from the Word and scanned files with Pandoc, reviewed by Philip), shown on a lesson page and saved offline as a plain self-contained page; Teacher's Information staff-only; no change-over period (amends FR-006, FR-023).
- `0013-lesson-suggestions-review-and-history.md` — lessons are changed by suggestions made from the lesson page in a word-processor-style editor, reviewed by one Approver as tracked changes (self-approval allowed), one open suggestion per lesson, and a permanent numbered version history as the audit trail; replaces the file-upload proposal workflow (amends 0012).
