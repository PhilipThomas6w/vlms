# Test Plan

Status: strategy (design gate) plus automated results (13/10/2026) — per ISO/IEC/IEEE 29119-3:2021. Manual results (UAT, production checks) are recorded in `quality/uat-report.md`.

## Approach & levels

- **Unit tests** (`Vlms.Tests`) — domain logic in `Vlms.Domain`: promotion rules, proposal state machine (propose/approve/reject/resubmit), consent-expiry blocking logic, at-risk threshold calculation.
- **Integration tests** — EF Core against a real (local/test) PostgreSQL instance or container (originally Azure SQL, per `adr/0005-hosting-pivot-self-hosted-vps-postgresql.md` — as-built, the suite instead uses SQLite in-memory for this layer, since the query-filter/audit-interceptor mechanism it's most concerned with is provider-independent; see `openwiki/testing.md`'s "Test-provider strategy" section): entity relationships, whole-entity access-restriction enforcement (`DbsCheck`, `ConsentSensitiveDetails`) per role, `ConsentRecord` (status/expiry) remaining readable to Teacher/Approver despite the restriction on its split-out sensitive sibling, certificate generation round-trip (QuestPDF → local disk storage).
- **End-to-end tests** — key Blazor user journeys per role (Teacher marks completion → promotion → certificate; Teacher proposes change → Approver approves/rejects → resubmission; Parent views own child only, not others; consent expiry blocks completion).
- **Accessibility testing** — WCAG 2.2 AA conformance check against core screens (automated tooling + manual spot-check), per `requirements/non-functional.md`.
- **Security testing** — OWASP ASVS 5.0-aligned checklist review at delivery gate, focused on access control (role/resource-based authorization) given the safeguarding data in scope.

## Environments

- Local development (solo developer machine).
- **No staging environment** — confirmed at design review, originally reasoned as an Azure App Service tier limitation (deployment slots require Standard tier or above, and ADR-0001 selected Basic/B1, which has none); the self-hosted VPS pivot (`adr/0005-hosting-pivot-self-hosted-vps-postgresql.md`) removed that specific constraint but didn't change the conclusion — a single low-cost VPS for a solo-maintained, tens-of-users system still has no separate staging host. Testing goes local/dev → production directly; this is an accepted trade-off for cost/complexity at this scale, not an oversight.

## Entry/exit criteria (initial)

- Entry: feature complete against its FR/NFR, unit tests passing locally.
- Exit (delivery gate): all TC-nnn below passing, no open Critical/High severity defects, WCAG 2.2 AA spot-check clean, access-control tests for DBS/consent masking passing.

## Initial test case skeleton (TC-nnn)

| TC | Scenario | Traces to |
|---|---|---|
| TC-001 | Teacher marks lesson complete; consent valid → completion recorded, certificate generated | FR (progress tracking), FR (certificates) |
| TC-002 | Teacher marks lesson complete; consent expired → blocked | FR (safeguarding & consent) |
| TC-003 | Student completes all active lessons in current rank → auto-promoted, badge awarded | FR (progress tracking) |
| TC-004 | Teacher proposes lesson change → Approver approves → live | FR (curriculum management) |
| TC-005 | Approver rejects with comments → Teacher resubmits → Approver approves | FR (curriculum management) |
| TC-006 | Parent views own child's dashboard; cannot view another parent's child | NFR (security/access control) |
| TC-007 | Teacher attempts to view a `DbsCheck` record → denied; Teacher can still read `ConsentRecord.Status`/`ExpiryDate` for the same student | NFR (security/access control), `adr/0004-sensitive-data-access-control.md` |
| TC-008 | Approver attempts to view/approve a `ConsentRecord` or `ConsentSensitiveDetails` → denied (out of role remit) | NFR (security/access control) |
| TC-009 | Consent/DBS expiry sweep flags and notifies correctly at threshold | FR (safeguarding & consent) |
| TC-010 | At-risk flag raised after 8 weeks with no completion | FR (reporting) |
| TC-011 | Any read of a `DbsCheck` or `ConsentSensitiveDetails` row writes a `SensitiveDataAccessLog` entry via the `IMaterializationInterceptor`, **including a multi-row read** (e.g. Safeguarding Officer lists several `DbsCheck` records) — one log entry per entity materialized, not just one for the query | `adr/0004-sensitive-data-access-control.md` |
| TC-012 | A `StudentGuardianLink` cannot be created by parent self-service — only by Admin/Teacher at registration | `data-design.md` (Guardian link verification) |
| TC-013 | A failed safeguarding-critical notification (expired consent/DBS) is retried, then escalated as a visible failure to Admin rather than silently dropped | `low-level-design.md` (NotificationService failure handling) |
| TC-014 | Attempting `UPDATE`/`DELETE` against `SensitiveDataAccessLog` as the application's database principal is denied | `adr/0004-sensitive-data-access-control.md` (tamper protection) |
| TC-015 | Parents get completion, promotion and consent-expiry emails: only linked, Admin-approved, not-left guardians; a failed send never fails the completion; reminders only on the scheduled days; no restricted data read | `functional.md` (Parent engagement), `low-level-design.md` (GuardianNotificationService) |
| TC-016 | Every role (Teacher, Approver, Admin, Student, Parent) can browse the lesson library and open or download a lesson file; an anonymous request for a file is refused | `functional.md` FR-006 |
| TC-017 | A teacher's uploaded file stays pending (visible to the proposer and Approvers only) until the Approver approves, then goes live; oversized or disallowed file types are rejected | `functional.md` FR-007 |
| TC-018 | A teacher marks several students complete for a lesson in one action; a student with lapsed consent is reported as blocked, not skipped | `functional.md` FR-008 |
| TC-019 | A teacher sees active students with current rank and progress, and can open a student's record | `functional.md` FR-009 |
| TC-020 | A student, their linked parent, a teacher and an Admin can download a certificate; an unlinked parent cannot | `functional.md` FR-010 |
| TC-021 | A student sees their own rank, completions, badges and certificates and no other student's | `functional.md` FR-011 |
| TC-022 | Each role's home page and navigation show only that role's actions | `functional.md` FR-012 |
| TC-023 | Admin and teacher edits to student details are saved; a teacher cannot change status | `functional.md` FR-013 |
| TC-024 | An Admin sets a rank badge image and it shows on dashboards | `functional.md` FR-014 |
| TC-025 | A parent submits the consent form for a linked child only; Safeguarding Officer or Admin approves or rejects with comments; the Approver role cannot; sensitive-detail reads are audit-logged | `functional.md` FR-015 |

Full RTM: `quality/traceability.md`.

## Results (delivery gate)

**Run:** `build/verify.ps1` (full gate) on 13/10/2026, 410/410 automated tests passing (`tests/Vlms.Tests`, xUnit). The gate also ran the secret scan, the OWASP ASVS 5.0 V8 access-control stage and the WCAG 2.2 AA stage, all passing. Re-run the gate on the release commit and update this line before the gate package is rendered.

**What "automated" covers here.** The tests exercise the service layer and EF Core query filters/interceptors against SQLite in-memory (see Approach). They do **not** drive the Blazor UI, and nothing has run against PostgreSQL or the production host. So each TC below has two parts: the automated evidence (done), and a manual confirmation in the real app (UAT, `quality/uat-report.md`) or on the server (`deploy/README.md` §7). A TC is **Passed** only when both are done.

| TC | Automated evidence (`tests/Vlms.Tests/…`) | Automated | Manual / production | Status |
|---|---|---|---|---|
| TC-001 | `CompletionServiceTests.MarkCompleteAsync_WithActiveConsent_RecordsCompletion_AndGeneratesCertificate`; `CertificateServiceTests.GenerateAsync_UploadsARealPdf_ToBlobStorage` | Pass | UAT-03 | Automated pass; UAT pending |
| TC-002 | `CompletionServiceTests.MarkCompleteAsync_WithExpiredConsent_Throws_AndRecordsNoCompletion`, `…_WithNoConsentRecord_Throws` | Pass | UAT-04 | Automated pass; UAT pending |
| TC-003 | `PromotionServiceTests.CheckAndPromoteAsync_AllActiveLessonsComplete_PromotesToNextRank`, `…_WithRankBadgeConfigured_AwardsStudentBadge`; `CompletionServiceTests.MarkCompleteAsync_CompletingFinalLessonInRank_TriggersAutoPromotion` | Pass | UAT-05 | Automated pass; UAT pending |
| TC-004 | `LessonProposalServiceTests.ProposeAsync_ByTeacher_CreatesPendingProposal`, `ApproveAsync_CreateProposal_CreatesLesson_AndApprovesProposal`, `ApproveAsync_EditProposal_UpdatesExistingLesson`, `ApproveAsync_RetireProposal_SetsLessonInactive` | Pass | UAT-06 | Automated pass; UAT pending |
| TC-005 | `LessonProposalServiceTests.RejectAsync_ByApprover_SetsRejectedStatus_AndStoresComments`, `ResubmitAsync_AfterRejection_CreatesNewProposal_ChainedToOriginal` | Pass | UAT-07 | Automated pass; UAT pending |
| TC-006 | `ParentDashboardServiceTests.GetDashboardAsync_ParentB_SeesOnlySam_NeverAlex_CrossParentAccessIsDenied`; `Authorization/StudentAccessHandlerTests.ParentHandler_Denies_UnlinkedStudent` | Pass | UAT-08 | Automated pass; UAT pending |
| TC-007 | `SensitiveDataAccessControlTests.Teacher_CannotRead_DbsCheck_Or_ConsentSensitiveDetails`, `Teacher_CanStillRead_ConsentRecord_StatusAndExpiry_ForSameStudent`; `DbsCheckServiceTests.RecordAsync_ByTeacher_Throws_AndCreatesNothing` | Pass | UAT-09 | Automated pass; UAT pending |
| TC-008 | `SensitiveDataAccessControlTests.Approver_CannotRead_DbsCheck_Or_ConsentSensitiveDetails_ButCanRead_ConsentRecord` (added 13/10/2026; fails when the query filter admits Approver); `ConsentRecordServiceTests.DecideAsync_ByApprover_Throws`, `RecordAsync_ByApprover_Throws_AndCreatesNothing` | Pass | UAT-09 | Automated pass; UAT pending |
| TC-009 | `ConsentExpiryJobTests.RunAsync_FlagsConsentExpiringSoon_Expired_AndMissing_ButNotBeyondWindowOrInactive`, `…_FlagsDbsExpiringSoon_Expired_Missing_AndFlaggedStatus_ButNotValid`, `RunAsync_ExpiryWindow_IsConfigurable`; `ConsentExpiryNotifierTests.NotifyAsync_ExpiredConsent_SendsOneDigestEmail_ToEachAdminAndSafeguardingOfficer_NotToTeacher` | Pass | `deploy/README.md` §7 (`vlms-jobs` run) | Automated pass; production run pending |
| TC-010 | `ConsentExpiryJobTests.RunAsync_FlagsAtRiskStudents_ByCompletionGap_OrEnrolmentIfNever_ButGivesNewEnrolmentsAFairChance`; `ProgressReportingServiceTests.GetAtRiskStudentsAsync_ProducesTheSamePopulation_AsConsentExpiryJobsSweep` | Pass | UAT-11 | Automated pass; UAT pending |
| TC-011 | `SensitiveDataAccessControlTests.MaterializingDbsCheck_AsAdmin_WritesAuditLogEntry_WithCorrectEntityId`, `MultiRowRead_OfTwoDbsChecks_WritesOneAuditLogEntryPerRow`; `ConsentExpiryJobTests.RunAsync_SweepingDbsChecks_WritesOneSensitiveDataAccessLogEntryPerDbsCheckRead` | Pass | UAT-10 | Automated pass; UAT pending |
| TC-012 | `GuardianLinkServiceTests.CreateLinkAsync_ByParent_Throws_AndCreatesNoLink`, `RegisterGuardianAndLinkAsync_ByParent_Throws_AndCreatesNoGuardianOrLink`; `StudentRegistrationServiceTests.RegisterStudentWithNewGuardianAsync_ByParent_Throws_AndCreatesNoRows` | Pass | UAT-02 | Automated pass; UAT pending |
| TC-013 | `NotificationServiceTests.SendAsync_SafeguardingCritical_AllAttemptsFail_ReturnsFailedAndEscalated_AndLogsError`, `…_SucceedsAfterRetries_ReturnsSentAfterRetry` | Pass | escalation visibility depends on the alert route (`operations/monitoring.md` §3, `[TBC – Philip]`) | Automated pass; alert route undecided |
| TC-014 | `MigrationsTests.MigrationScript_RevokesUpdateAndDeleteFromPublic`, `MigrationScript_EnablesAndForcesRowLevelSecurity`, `MigrationScript_DoesNotDefinePermissiveUpdateOrDeletePolicy` (generated SQL only) | Pass | `deploy/README.md` §3 live `UPDATE` as `vlms_app` and `vlms_owner` must fail | Script verified; live check pending go-live |
| TC-015 | `GuardianNotificationServiceTests` (17 cases, added 20/10/2026): `Completion_EmailsOnlyLinkedApprovedNotLeftGuardians`, `CompletingTheRank_AlsoEmailsThePromotion`, `FailingSend_DoesNotFailOrUndoTheCompletion`, `Reminder_SentOnScheduledDays`, `Reminder_NotSentOnOtherDays`, `LapsedConsent_DayAfterExpiry_SendsSafeguardingCriticalNotice`, `Notifications_ReadNoRestrictedData`, role gates | Pass | first real send pending go-live (Graph secret) | Automated pass; live send pending go-live |
| TC-020 | `CertificateAccessServiceTests` (owning student, linked parent, Teacher, Admin open the PDF; unlinked parent, parent who has left, another student, Approver, Safeguarding Officer, Admin without MFA and unknown id get null; reversed-completion certificate hidden from student and parent only); `CertificateEndpointTests` (inline with `nosniff`, `?download=1` as attachment, refusal is 404, Approver 403 at the policy) | Pass | UAT: open a real generated PDF in a browser as each role | Automated pass; UAT pending |

**Not covered by the TC list, recorded for completeness:** the end-to-end Blazor journeys named under Approach are covered by UAT, not by automated browser tests (none were built: a deliberate cost decision at this scale). WCAG 2.2 AA: automated gate stage plus `quality/wcag-2.2-aa-checklist.md`; colour contrast (1.4.3/1.4.11) is still open pending branding.

**Exit-criteria position (13/10/2026):** not yet met. Open: UAT, the production-only checks for TC-009/TC-014, colour contrast, and the TC-013 alert route.
