# Calendar (FR-022)

Requirement: `docs/requirements/functional.md` FR-022. The Church's liturgical calendar (New Calendar, from the Greek Orthodox Archdiocese of America's ICS feed) alongside each chapter's events (the days it meets). Registers per class per event: [attendance.md](attendance.md).

## Data

- `Vlms.Domain/ChapterEvent.cs` (`IChapterOwned`, top-level, unique per chapter per date): date, name, optional start/end time and place. Chapter filter arm only; anyone working in the chapter reads them.
- `LiturgicalDay.cs` (national, key = date): title, `LiturgicalFast`, saints; `LiturgicalReading` (heading, reference, text, order). Listed as national in `ChapterSeparationTests`.
- `CalendarFeedToken.cs` (per person per chapter, not chapter-owned, like `ActiveChapterChoice`): SHA-256 hash of the subscribe link's secret.

## `Calendar/`

- `LiturgicalFeedParser`: unfolds and unescapes the ICS; per VEVENT, the `DTSTART` date, `SUMMARY` without the leading fasting emoji, and the description's paragraphs: "Saints and Feasts:", the fast line (`Fast Free`, `Fast Day (... Allowed)`, `Strict Fast`; none = `LiturgicalFast.None`), and "... Reading(s): reference" plus text until the next reading.
- `LiturgicalCalendarImporter`: system principal only; downloads (`Calendar:LiturgicalFeedUrl`, default the GOARCH feed), parses, and in one transaction replaces every day in the feed's range. Run by `Vlms.Jobs` after the daily sweep, and by `Vlms.Jobs import-calendar`; a failure is logged and sets exit code 1, keeping the days already stored.
- `CalendarService`: `CanRead` (any chapter role in the active chapter); `GetMonthAsync`, `GetUpcomingAsync`, `GetLiturgicalDayAsync`, `GetEventAsync`; Admin-only `CreateEventAsync`/`UpdateEventAsync`/`DeleteEventAsync` (one a day; no date change or removal once a register exists).
- `CalendarFeedService`: `GetLinkCreatedAtAsync`, `CreateLinkAsync` (returns the secret once), `RemoveLinkAsync` for the caller's active chapter; `BuildFeedAsync(secret)` (no sign-in) finds the hash, checks the owner still belongs to the chapter (`ChapterMembership.ChapterIds`), reads events through a short-lived `CalendarFeedUserContext` for that chapter, and writes iCalendar (UK times as UTC, all-day events without times, escaping, 75-octet folding).

## Web

- `Pages/Calendar/Calendar.razor` (`/calendar`, `RequireChapterMember`, nav "Calendar"): the month as a list of days, `?month=yyyy-MM`; feast, `Ui/FastPill`, events; "Saints and readings" expands per day; the subscribe link panel.
- `Pages/Calendar/EventPage.razor` (`/calendar/events/{id}`, `RequireChapterMember`): event details; class registers and the day total for Teachers and Admins.
- `Pages/Calendar/EditEvent.razor` (`/calendar/events/new`, `/calendar/events/{id}/edit`, `RequireAdmin`).
- `CalendarFeedEndpoint` (`GET /calendar/feed/{secret}.ics`, anonymous, 404 unless valid, `Cache-Control: private, no-store`).
- Policy `RequireChapterMember` (`Program.cs`): Admin, Teacher, Safeguarding Officer, Parent or Student in the active chapter, plus the MFA rule.

## Tests

`LiturgicalCalendarTests` (TC-053), `CalendarServiceTests` (TC-054), `CalendarFeedServiceTests` (TC-055), `ChapterSeparationTests` (17 chapter-owned entities); the design capture covers the `/calendar` pages for student, parent, teacher and admin.
