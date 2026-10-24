# Attendance register (FR-020)

Design: `docs/design/chapters-and-attendance.md` sections 3, 5 and 7 (assumptions as built). Requirement: `docs/requirements/functional.md` FR-020.

## Data

- `Vlms.Domain/AttendanceRegister.cs`, `AttendanceMark.cs`; both `IChapterOwned`. Register is top-level (stamped with the active chapter by `ChapterWriteRules`); a mark takes its register's chapter and must match its student's. Registers have only the chapter arm of the filter; marks also have the own-records arm (linked parent, the student). Migration `AddAttendance`.

## `Attendance/AttendanceService`

- `GetRegisterAsync(date)` (Teacher or Admin): active students plus anyone already on that register, their mark, consent in force.
- `SaveRegisterAsync(date, marks)` (Teacher): creates or corrects the register; refuses a future date, an empty register, and any student who is not active in the caller's chapter (the chapter filter hides the rest). Returns counts and `PresentWithoutConsent` (newly present on this register, no approved consent in force). Ids via `NextIdAsync`.
- `ListRegistersAsync`, `GetMarksAsync(date)` (Record completions), `GetStudentHistoryAsync(id)` (staff record, active students, last 12 months); internal `LoadHistoryAsync` shared with `ParentDashboardService.GetAttendanceAsync` (linked children only).

## `Attendance/AttendanceNotificationService`

Emails the active chapter's Safeguarding Officers (`ChapterMembership.StaffIn`) when children without consent in force are marked present: names and date only, `SafeguardingCritical` priority, never throws.

## Elsewhere

- `AtRiskStudentFlagging`: also flags a student whose latest two marks are both absent (`ConsecutiveAbsenceThreshold`); `AtRiskStudentFlag.NoRecentCompletion` / `AbsentFromLastMeetings` say which rule; shown as "Reason" on `/reporting`, logged by `ConsentExpiryJob`.
- `StudentRosterService.ListActiveStudentsAsync`: `RecentAttended` of `RecentMeetings` (latest 3 marks) on `/students`.
- `NationalTotalsService`: a second fixed aggregate SQL statement (chapter id, meeting date, counts), summed per month for the last 12; `/reporting/national`.

## Pages

- `Pages/Attendance/TakeRegister.razor` (`/attendance`, `RequireTeacher`; the service also lets Admins read, for the student record): date picker (`?date=`), tick who is here, consent warning per child, registers taken, link to `/progress/record?date=`.
- `Progress/RecordCompletions.razor`: reads the meeting's marks (`?date=`, default today); "Here"/"Absent" pills; "Select everyone here and ready" skips absent children; recording an absent child needs the catch-up confirmation.
- `Ui/AttendanceSummary.razor`: 12-month summary on `/students/{id}` and per child on `/parent/dashboard`.

## Tests

`AttendanceServiceTests` (TC-052), `ChapterSeparationTests` (register and mark per chapter), `ParentDashboardServiceTests.GetAttendanceAsync_*`, `StudentRosterServiceTests.List_ShowsRecentAttendance_*`; browser: `Vlms.UiTests/AttendanceJourneyTests` (gate's ui-tests stage).
