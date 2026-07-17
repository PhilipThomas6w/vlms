# Requirements Traceability Matrix

Status: initial (design gate) — completed at delivery gate.

| BR | FR/NFR area | Design ref | TC | Status |
|---|---|---|---|---|
| BR-001 Progress tracking | Lesson completion, auto-promotion (`functional.md`) | `data-design.md` (StudentLessonCompletion, StudentRankProgress), `low-level-design.md` (PromotionService) | TC-001, TC-003 | Designed |
| BR-002 Curriculum management | Propose/approve/reject/resubmit (`functional.md`) | `data-design.md` (LessonChangeProposal), `low-level-design.md` (LessonProposalService) | TC-004, TC-005 | Designed |
| BR-003 Safeguarding & consent | Annual consent, DBS, expiry blocking (`functional.md`) | `data-design.md` (ConsentRecord, DbsCheck), `low-level-design.md` (ConsentExpiryJob) | TC-002, TC-009 | Designed |
| BR-004 Certificates & badges | Auto-generated PDF, tracked badge records (`functional.md`) | `data-design.md` (Certificate, RankBadge, StudentBadge), `low-level-design.md` (CertificateService) | TC-001, TC-003 | Designed |
| BR-005 Parent engagement | Dashboard + notifications (`functional.md`) | `low-level-design.md` (NotificationService) | TC-006 | Designed |
| BR-006 Reporting | Core reports + at-risk flagging (`functional.md`) | `low-level-design.md` (ConsentExpiryJob at-risk sweep) | TC-010 | Designed |
| NFR-001 Access control / PII | Role-based + resource-based authorization, DBS/consent masking (`non-functional.md`, `governance/security-compliance.md`) | `low-level-design.md` (authorization model) | TC-006, TC-007, TC-008 | Designed |
| NFR-002 Accessibility | WCAG 2.2 AA (`non-functional.md`) | — (applies across UI) | (accessibility test pass) | Planned |
| NFR-003 Availability | Best-effort (`non-functional.md`) | `adr/0001-technology-stack.md` (App Service tier) | — | Accepted, not test-gated |

Note: `BR-nnn` identifiers here are provisional groupings assigned during design to enable traceability; formalise as discrete numbered BR-nnn/FR-nnn entries in `requirements/business-requirements.md` / `functional.md` if finer-grained tracking is needed before build.
