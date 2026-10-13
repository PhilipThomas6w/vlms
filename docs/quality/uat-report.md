# User Acceptance Test report (VLMS-UAT)

Status: **template**, not yet run. Fill in during UAT on the production system after go-live (there is no staging environment; `quality/test-plan.md` Environments). Use test accounts and fictional students only; delete them afterwards, or keep them marked as test data `[TBC – Philip]`.

## 1. Scope and approach

- **What is tested:** each role's core journey in the real app, through the browser, on the production hostname. This is the manual half of TC-001…TC-014 (`quality/test-plan.md` Results) plus the end-to-end journeys the automated suite does not drive.
- **Devices:** at least one phone (iOS or Android) and one desktop browser, per NFR-004.
- **Entry criteria:** release deployed; `deploy/README.md` §7 checklist complete; seed rank ladder and curriculum loaded (`STATE.md` Blocked: seed data); test accounts for each role, with MFA enrolled for Admin and Safeguarding Officer.
- **Exit criteria:** every scenario below Passed or accepted with a recorded workaround; no open Critical/High defects.

## 2. Participants

| Role tested | Tester | Date |
|---|---|---|
| Admin | `[TBC – Philip]` | |
| Safeguarding Officer | `[TBC – Philip]` | |
| Teacher | `[TBC – Philip]` | |
| Approver | `[TBC – Philip]` | |
| Parent | `[TBC – Philip]` | |

Ideally at least one real teacher and one real parent, not only the developer.

## 3. Scenarios

Result: Pass / Fail / Blocked. Record a defect ID for every Fail.

| ID | Role | Scenario | Expected | TC | Result | Notes / defect |
|---|---|---|---|---|---|---|
| UAT-01 | Admin | Sign in with MFA; open user roles; approve a waiting guardian as Parent | Role granted; parent can sign in and sees their child | — | | |
| UAT-02 | Teacher | Register a new student with a new guardian; guardian receives invite email and accepts | Student and link created; invite email arrives from `noreply@varangians.co.uk`; no role until Admin approves | TC-012 | | |
| UAT-03 | Teacher | Mark a lesson complete for a student with valid consent; download certificate | Completion recorded; PDF certificate opens and is correct | TC-001 | | |
| UAT-04 | Teacher | Try to mark a lesson complete for a student with expired or no consent | Blocked with a clear message; nothing recorded | TC-002 | | |
| UAT-05 | Teacher | Complete the last active lesson in a rank | Student promoted; badge shown on the parent dashboard (no promotion email: not built, see RTM BR-005) | TC-003 | | |
| UAT-06 | Teacher, Approver | Propose a lesson change; Approver approves | Change live in the curriculum | TC-004 | | |
| UAT-07 | Teacher, Approver | Approver rejects with comments; Teacher resubmits; Approver approves | Comments visible to Teacher; resubmission linked; change live | TC-005 | | |
| UAT-08 | Parent | View own child's dashboard; try another child's URL | Own child shown; other child refused | TC-006 | | |
| UAT-09 | Teacher, Approver | Try to open DBS checks and consent sensitive details (menu and direct URL) | Not offered and refused; consent status/expiry still visible to Teacher | TC-007, TC-008 | | |
| UAT-10 | Safeguarding Officer | Record a DBS check; view a consent record's sensitive details | Visible; each read appears in the access log | TC-011 | | |
| UAT-11 | Admin | Open reports: rank stats, promotion history, at-risk list, records due for deletion | Figures match the test data | TC-010 | | |
| UAT-12 | Teacher | Reverse own completion within the window | Completion reversed; certificate hidden from parent | — | | |
| UAT-13 | Any | Forgot password; reset by email; sign in | Email arrives; reset works; old password refused | — | | |
| UAT-14 | Parent | Install the app to the phone home screen (PWA) | Installs and opens on the sign-in page | — | | |
| UAT-15 | Any | Keyboard-only and screen-reader spot check on sign-in, dashboard and completion form | Usable; focus visible; messages announced | NFR-002 | | |

## 4. Defects

| ID | Scenario | Severity (Critical/High/Medium/Low) | Description | Status | Fixed in |
|---|---|---|---|---|---|
| | | | | | |

## 5. Outcome and sign-off

- Summary: `[TBC – after UAT]`
- Open defects accepted for release, with reason: `[TBC – after UAT]`
- Recommendation (release / release with conditions / do not release): `[TBC – after UAT]`

| Sign-off | Name | Date |
|---|---|---|
| Project owner | `[TBC – Philip]` | |
| Safeguarding Officer (for UAT-04, -09, -10) | `[TBC – Philip]` | |
