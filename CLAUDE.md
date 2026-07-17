# CLAUDE.md — Varangians LMS operating principles

Operating principles for this project, per the `software-design-docs` skill.

- **Verify, don't assert.** For Microsoft/.NET technology and platform decisions, use the Microsoft Learn MCP tools (and web search where needed) to check before advising; cite sources.
- **Persist every decision.** No decision exists until it is written into the relevant `docs/` markdown file, in the same turn it is agreed. The working markdown is the source of truth, not the conversation.
- **Requirements before solutions.** Capture the underlying business need in `business-requirements.md` / `functional.md`, not a specific technology choice. Push back and name the conflation when a stated requirement is actually a solution in disguise.
- **Document style pack:** `generic-docx` (neutral, unbranded, A4/Arial) — confirmed with the project owner at kickoff. Used by `/render-gate-package`.
- **Project document code:** `VLMS` (project name: Varangians LMS).
- **Gate packages** (design, delivery) are rendered only from `docs/` markdown via `/render-gate-package`; never hand-authored.
- **Provenance:** this is a fresh .NET implementation. Prior discovery/requirements material was extracted from an abandoned Power Platform-based build of the same project; that tooling is deliberately not being reused, but the underlying business requirements it captured are the starting point for this discovery.
