# CLAUDE.md — Varangians LMS operating principles

Operating principles for this project, per the `software-design-docs` skill.

- **Verify, don't assert.** For Microsoft/.NET technology and platform decisions, use the Microsoft Learn MCP tools (and web search where needed) to check before advising; cite sources.
- **Persist every decision.** No decision exists until it is written into the relevant `docs/` markdown file, in the same turn it is agreed. The working markdown is the source of truth, not the conversation.
- **Requirements before solutions.** Capture the underlying business need in `business-requirements.md` / `functional.md`, not a specific technology choice. Push back and name the conflation when a stated requirement is actually a solution in disguise.
- **Document style pack:** `generic-docx` (neutral, unbranded, A4/Arial) — confirmed with the project owner at kickoff. Used by `/render-gate-package`.
- **Project document code:** `VLMS` (project name: Varangians LMS).
- **Gate packages** (design, delivery) are rendered only from `docs/` markdown via `/render-gate-package`; never hand-authored.
- **Provenance:** this is a fresh .NET implementation. Prior discovery/requirements material was extracted from an abandoned Power Platform-based build of the same project; that tooling is deliberately not being reused, but the underlying business requirements it captured are the starting point for this discovery.

## Project law

- **Hard constraints:** see `docs/VISION.md`. In brief — Blazor Web App + Azure (App Service Basic/B1, no staging slot) + Entra External ID + Azure SQL + Blob Storage + Communication Services Email + QuestPDF (`docs/adr/0001`); `DbsCheck`/`ConsentSensitiveDetails` are whole-entity restricted to Admin/Safeguarding Officer only via EF Core global query filters, never column-level masking (`docs/adr/0004`); the Approver role is curriculum-only, never conflated with safeguarding/consent sign-off; every read of `DbsCheck`/`ConsentSensitiveDetails` is audit-logged via `IMaterializationInterceptor`; `StudentGuardianLink` is created only by Admin/Teacher, never parent self-service; retention is 3 years (students) / 6 years (DBS, audit log), hard delete not anonymisation.
- **Done means `build/verify.ps1` exits 0.** Not "looks right", not a partial test run — the gate, in full (or `-Fast` for a quick local check), deciding pass/fail.
- **Local gate commands:** `pwsh -File build/verify.ps1` (full), `pwsh -File build/verify.ps1 -Fast` (quick). Stages: build (`dotnet build -warnaserror`), test (`dotnet test`), secrets scan. WCAG/OWASP ASVS review stages are named but not yet wired — see `STATE.md` Next item 10.
- **Model routing:** default Sonnet 5 at medium effort for implementation work. Escalate genuinely hard reasoning (e.g. the access-control/query-filter design) to the strongest model available. Escalate anything cybersecurity-relevant to Opus. Cheap read-only mapping/exploration can go to a small model.
- **State lives on disk:** `docs/VISION.md` (standing spec), `STATE.md` (the queue, reread every session), `docs/harness/LEDGER.csv` (token/outcome ledger, written by the verify gate — don't self-estimate).
- **When you need codebase context, check `openwiki/` first** — it's the current as-built map of the code (what's actually there, not just design intent). Start at `openwiki/index.md`. Refresh it (`loop-harness:doc-refresh`) after a work-next cycle changes the shape of the code.
