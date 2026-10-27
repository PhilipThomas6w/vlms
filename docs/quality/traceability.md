# Requirements Traceability Matrix

Status: delivery gate, completeness check 13/10/2026. Status values: **Verified (automated)** = built and passing in `build/verify.ps1`, manual UAT pending; **Partial** = some of the requirement is not built; **Pending go-live** = can only be checked on the production host.

| BR | FR/NFR area | Design ref | TC | Status |
|---|---|---|---|---|
| BR-001 Progress tracking | Lesson completion, auto-promotion (`functional.md`) | `data-design.md` (StudentLessonCompletion, StudentRankProgress), `low-level-design.md` (PromotionService) | TC-001, TC-003 | **Partial (22/10/2026):** services verified (automated), but no page records a completion (FR-008) |
| BR-002 Curriculum management | Suggest, review, approve or send back, publish now, refer to committee, Approver alerts, version history (`functional.md` FR-007, `adr/0013`) | `data-design.md` (LessonSuggestion, LessonSuggestionApproval, LessonVersion), `low-level-design.md` (LessonSuggestionService) | TC-004, TC-005, TC-017, TC-057 | Verified (automated) |
| BR-003 Safeguarding & consent | Annual consent, DBS, expiry blocking, guardian-link verification (`functional.md`) | `data-design.md` (ConsentRecord, ConsentSensitiveDetails, DbsCheck, StudentGuardianLink), `low-level-design.md` (ConsentExpiryJob) | TC-002, TC-009, TC-012, TC-013, TC-014, TC-058 | Verified (automated); TC-014 and TC-058 live checks pending go-live |
| BR-004 Certificates & badges | Auto-generated PDF, tracked badge records (`functional.md`) | `data-design.md` (Certificate, RankBadge, StudentBadge), `low-level-design.md` (CertificateService) | TC-001, TC-003 | **Partial (22/10/2026):** PDFs generated, but no one can download them (FR-010); badge images: Admin upload built (FR-014), awaiting Philip's live check |
| BR-005 Parent engagement | Dashboard + notifications (`functional.md`) | `low-level-design.md` (NotificationService, GuardianNotificationService) | TC-006, TC-013, TC-015 | Verified (automated); proactive parent emails built 20/10/2026 (`GuardianNotificationService`); first live send pending go-live |
| BR-007 Lesson content access and usability by role (22/10/2026) | FR-006 to FR-016 (`functional.md`) | `requirements/user-journeys.md` | TC-016 to TC-026 | **Not built** |
| BR-006 Reporting | Core reports + at-risk flagging (`functional.md`) | `low-level-design.md` (ConsentExpiryJob at-risk sweep) | TC-010 | Verified (automated) |
| BR-003 Safeguarding & consent | FR-017 leavers; staff step down and are reinstated (amended 27/10/2026) | `adr/0014` decision 3, `Identity/LeftStaffPolicy`, `Retention/LeavingService`, `StaffStepDown` | TC-038 | Verified (automated, 27/10/2026); browser walk pending |
| NFR-001 Access control / PII | Role-based + resource-based authorization, DBS/consent-sensitive-detail whole-entity restriction, read audit log (`non-functional.md`, `governance/security-compliance.md`) | `low-level-design.md` (authorization model), `adr/0002`, `adr/0004` | TC-006, TC-007, TC-008, TC-011, TC-014, TC-047a (MFA for staff in any chapter, `adr/0010` assumption 9, 27/10/2026) | Verified (automated) + ASVS 5.0 V8 gate stage; countersign pending |
| NFR-002 Accessibility | WCAG 2.2 AA (`non-functional.md`) | — (applies across UI) | WCAG gate stage; `quality/wcag-2.2-aa-checklist.md`; UAT-15 | Partial: colour contrast (1.4.3/1.4.11) open pending branding |
| NFR-003 Availability | Best-effort (`non-functional.md`) | `adr/0005-hosting-pivot-self-hosted-vps-postgresql.md` (single VPS); `operations/dr-bcp.md` | — | Accepted, not test-gated; RPO/RTO stated in `operations/dr-bcp.md` §1 |
| NFR-004 Compatibility | Named browser/OS/PWA target (`non-functional.md`) | `adr/0001-technology-stack.md` (Blazor) | UAT (phone + desktop) | Pending UAT |

Note: `BR-nnn` identifiers here are provisional groupings assigned during design to enable traceability; formalise as discrete numbered BR-nnn/FR-nnn entries in `requirements/business-requirements.md` / `functional.md` if finer-grained tracking is needed before build.

## FR-level trace (safeguarding & consent)

| FR | Built in | TC | Status |
|---|---|---|---|
| FR-001 Annual consent record, approved by SO/Admin only | `ConsentRecordService` | TC-008 (`ConsentRecordServiceTests`) | Verified (automated) |
| FR-002 DBS tracking, Admin/SO only | `DbsCheckService`, query filters | TC-007, TC-008 | Verified (automated) |
| FR-003 Expiry monitoring, escalating alerts, expired consent blocks completion | `ConsentExpiryJob`, `ConsentExpiryNotifier`, `CompletionService` | TC-002, TC-009, TC-013 | Verified (automated); escalation reaches Admin/SO by email and log, alert route for failures `[TBC – Philip]` |
| FR-004 Guardian link by Admin/Teacher only; invite grants no role; link to an existing staff-entered guardian at final approval (`adr/0008` decision 8) | `GuardianLinkService`, `StudentRegistrationService`, `RoleAssignmentService`, `StudentApplicationService` | TC-012, TC-039a | Verified (automated) |
| FR-005 Every sensitive read audit-logged | `IMaterializationInterceptor` audit | TC-011, TC-014 | Verified (automated); TC-014 live check pending go-live |

## Completeness check (13/10/2026)

- Every BR and NFR row has a design reference and either a TC or an explicit "not test-gated" reason. Every TC-001…TC-015 traces back to at least one row.
- **Gaps found:**
  1. ~~**BR-005 proactive parent notifications not built.**~~ Closed 20/10/2026: `GuardianNotificationService`, TC-015.
  2. ~~**Retention purge not built** (BR-003; `STATE.md` Blocked).~~ Closed 27/10/2026: `adr/0014`, TC-058.
  3. **Narrative requirements without FR numbers** (curriculum, progress, parent, reporting) are traced at BR level only, as the note below allows.
