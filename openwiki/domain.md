# Vlms.Domain

Plain C# entities and abstractions, no EF Core or ASP.NET Core dependency. Source of truth for fields/relationships is `docs/design/data-design.md` — this page is a map into the code, not a restatement.

## Entities (17, one file each in `src/Vlms.Domain/`)

`Rank`, `Lesson`, `LessonChangeProposal`, `Student`, `ParentGuardian`, `StudentGuardianLink`, `StudentLessonCompletion`, `Certificate`, `RankBadge`, `StudentBadge`, `StudentRankProgress`, `ConsentRecord`, `ConsentSensitiveDetails`, `DbsCheck`, `AppUser`, `UserRole`, `GuardianInvite`, `SensitiveDataAccessLog`.

Three things worth knowing that aren't obvious from `data-design.md` alone:

- **`ConsentRecord` vs `ConsentSensitiveDetails` are deliberately separate entities**, not a naming quirk. `ConsentRecord` holds status/expiry/non-sensitive consent flags (readable by Teacher/Approver); `ConsentSensitiveDetails` holds the medical/dietary-SEN/emergency-contact fields (restricted, see [access-control.md](access-control.md)). This split exists because EF Core query filters are row-level, not column-level — see `docs/adr/0004-sensitive-data-access-control.md` for the full story of why the original single-entity design was wrong.
- **`Student.AppUserId` and `ParentGuardian.AppUserId`** (both nullable `int?`) were added during the authentication/authorization work, not in the original design gate package — they support self-login (Student) and parent-login (ParentGuardian). Documented in `docs/design/data-design.md`'s entity table; see [authentication-authorization.md](authentication-authorization.md).
- **`GuardianInvite` is new** (STATE.md "Identity" item, replacing the Microsoft Graph guardian-invitation mechanism with a self-rolled signed/single-use/expiring link). It does not itself carry the token — the token is an ASP.NET Core Data Protection `ITimeLimitedDataProtector`-protected wrapper around this row's `Id`, generated fresh on issue and never persisted. See [guardian-links.md](guardian-links.md) for the full flow and [authentication-authorization.md](authentication-authorization.md) for the `AppUser`/Identity coexistence design it depends on.
- **`Student.LeftAt` (nullable `DateTime?`) and `AppUser.HasLeft`/`AppUser.LeftAt`** (STATE.md "Manual 'leaving' flag for students and teachers, feeding the retention report") — set only by the new Admin-only `Retention.LeavingService` (`MarkStudentInactiveAsync`/`MarkTeacherLeftAsync`), never self-service, and captured automatically as the action's own timestamp rather than a manually entered/backdated date. `AppUser.HasLeft`/`LeftAt` are deliberately generic to *any* `AppUser` row, not a new Teacher-specific entity — following the precedent `DbsCheck.TeacherUserId` already set (a plain `AppUser` validated Teacher-role-holding at write time, not a dedicated Teacher type); the *action* is Teacher-scoped (`MarkTeacherLeftAsync` validates `Role.Teacher`), the *schema* is not. See [admin-reporting.md](admin-reporting.md)'s "Records due for deletion" section for how `RetentionFlagging` now uses both fields.

All entity primary keys are application-assigned (`ValueGeneratedNever` in `VlmsDbContext`), except `SensitiveDataAccessLog.Id` and `VlmsIdentityUser.Id` (`Vlms.Infrastructure/Identity/`, ASP.NET Core Identity's own store row, not a `Vlms.Domain` entity — see [authentication-authorization.md](authentication-authorization.md)), both database-generated (`ValueGeneratedOnAdd`) — see [data-access.md](data-access.md) for why. `AppUser.Id` stays `ValueGeneratedNever`, but its *value* is always copied from the paired `VlmsIdentityUser.Id` immediately after that row is inserted, not computed independently.

## `Role` (`src/Vlms.Domain/Role.cs`)

Enum: `Admin`, `Teacher`, `Approver`, `Parent`, `Student`, `SafeguardingOfficer`. A single `AppUser` may hold more than one `UserRole` row (e.g. Teacher + Approver). **The `Approver` role is curriculum-approval only** — it has no consent/DBS/safeguarding privileges; do not add any authorization check that grants `Approver` access to `ConsentSensitiveDetails`, `DbsCheck`, or consent approval. This was an explicit discovery-phase correction (`docs/requirements/stakeholders.md`), not an oversight if you're tempted to "simplify" it.

## `ICurrentUserContext` (`src/Vlms.Domain/ICurrentUserContext.cs`)

```csharp
public interface ICurrentUserContext
{
    int? UserId { get; }
    bool HasRole(Role role);
    bool HasMfaEnabled { get; }
}
```

`HasMfaEnabled` is new (STATE.md "Identity" item 4) — whether the caller has TOTP MFA enabled on their ASP.NET Core Identity credential row. Consulted by `MfaRequirementHandler` and by `VlmsDbContext`'s `DbsCheck`/`ConsentSensitiveDetails` query filters, which now require it in addition to role — see [authentication-authorization.md](authentication-authorization.md)'s MFA section for the full "enforced for Admin/SafeguardingOfficer, offered to everyone" scope decision.

Consumed by `VlmsDbContext`'s query filters and by `SensitiveDataAuditInterceptor` (both in `Vlms.Infrastructure`). Three implementations exist, all in Infrastructure — see [data-access.md](data-access.md) and [authentication-authorization.md](authentication-authorization.md).
