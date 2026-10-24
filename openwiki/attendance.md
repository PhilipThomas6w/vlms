# Attendance registers (FR-020, FR-022)

Design: `docs/design/chapters-and-attendance.md` sections 3, 5 and 7 (assumptions as built). Requirements: `docs/requirements/functional.md` FR-020 and FR-022 (one register per class per meeting day; events: [calendar.md](calendar.md)).

## Data

- `Vlms.Domain/AttendanceRegister.cs`, `AttendanceMark.cs`; both `IChapterOwned`. A register belongs to a `ChapterEvent` and a `Rank` (unique per event and rank) and takes the event's chapter (`ChapterWriteRules`, stamped before its marks); a mark takes its register's chapter and must match its student's. `MeetingDate` is copied onto register and mark; marks are unique per student per date. Registers have only the chapter arm of the filter; marks also have the own-records arm (linked parent, the student). `Vlms.Domain/ClassTeacher.cs`: Teacher-to-class (rank) assignments. Migration `AddCalendarAndAttendance` (replaced the never-applied `AddAttendance`).

## `Attendance/AttendanceService`

- `GetEventRegistersAsync(eventId)` (Teacher or Admin): every class with active students or a register at the event, the caller's own classes (`ClassTeacher`) first, each with counts and teachers; `Present`, `Absent` and `ClassesTaken` collate the day. Null for another chapter's event.
- `GetRegisterAsync(eventId, rankId)`: active students at that rank not already marked on another class's register that day, plus anyone already on this register.
- `SaveRegisterAsync(eventId, rankId, marks)` (any Teacher): creates or corrects the class's register; refuses an event not in the chapter, a future event, an empty register, a student on another class's register that day, and any student not in this class (active, current rank) or already on it. Returns counts and `PresentWithoutConsent`. Ids via `NextIdAsync`.
- `ListMeetingDaysAsync` (from 12 months ago onwards, collated), `GetMarksAsync(date)` (Record completions, every class), `GetStudentHistoryAsync(id)`; internal `LoadHistoryAsync` shared with `ParentDashboardService.GetAttendanceAsync`.

## `Attendance/ClassTeacherService`

Admin only: `ListAsync` (every rank and its teachers in the chapter), `ListTeachersAsync` (non-leaver Teachers of the chapter, `ChapterMembership.StaffIn`), `AssignAsync`, `RemoveAsync`.

## `Attendance/AttendanceNotificationService`

Emails the active chapter's Safeguarding Officers (`ChapterMembership.StaffIn`) when children without consent in force are marked present: names and date only, `SafeguardingCritical` priority, never throws.

## Elsewhere

- `AtRiskStudentFlagging`: also flags a student whose latest two marks are both absent (`ConsecutiveAbsenceThreshold`); `AtRiskStudentFlag.NoRecentCompletion` / `AbsentFromLastMeetings` say which rule; shown as "Reason" on `/reporting`, logged by `ConsentExpiryJob`.
- `StudentRosterService.ListActiveStudentsAsync`: `RecentAttended` of `RecentMeetings` (latest 3 marks) on `/students`.
- `NationalTotalsService`: a second fixed aggregate SQL statement (chapter id, meeting date, counts), summed per month for the last 12; `/reporting/national`.

## Pages

- `Pages/Attendance/TakeRegister.razor` (`/attendance/{eventId}/{rankId}`, `RequireTeacher`): one class's register, opened from the event page; tick who is here, consent warning per child, link to `/progress/record?date=`.
- `Pages/Attendance/Registers.razor` (`/attendance`, `RequireAdminOrTeacher`, nav "Registers"): meeting days coming up, and past days with their collated totals.
- `Pages/Calendar/EventPage.razor` (`/calendar/events/{id}`): for Teachers and Admins, the class registers and the day's total ([calendar.md](calendar.md)).
- `Administration/UserRoles.razor`: the "Class teachers" section.
- `Progress/RecordCompletions.razor`: reads the meeting's marks (`?date=`, default today); "Here"/"Absent" pills; "Select everyone here and ready" skips absent children; recording an absent child needs the catch-up confirmation.
- `Ui/AttendanceSummary.razor`: 12-month summary on `/students/{id}` and per child on `/parent/dashboard`.

## Tests

`AttendanceServiceTests` (TC-052), `CalendarServiceTests` (class teachers), `ChapterSeparationTests` (event, class teacher, register and mark per chapter), `ParentDashboardServiceTests.GetAttendanceAsync_*`, `StudentRosterServiceTests.List_ShowsRecentAttendance_*`; browser: `Vlms.UiTests/AttendanceJourneyTests` (gate's ui-tests stage).
