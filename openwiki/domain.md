# Vlms.Domain

Plain C# entities and abstractions, no EF Core or ASP.NET Core dependency. Source of truth for fields/relationships is `docs/design/data-design.md` — this page is a map into the code, not a restatement.

## Entities (16, one file each in `src/Vlms.Domain/`)

`Rank`, `Lesson`, `LessonChangeProposal`, `Student`, `ParentGuardian`, `StudentGuardianLink`, `StudentLessonCompletion`, `Certificate`, `RankBadge`, `StudentBadge`, `StudentRankProgress`, `ConsentRecord`, `ConsentSensitiveDetails`, `DbsCheck`, `AppUser`, `UserRole`, `SensitiveDataAccessLog`.

Two things worth knowing that aren't obvious from `data-design.md` alone:

- **`ConsentRecord` vs `ConsentSensitiveDetails` are deliberately separate entities**, not a naming quirk. `ConsentRecord` holds status/expiry/non-sensitive consent flags (readable by Teacher/Approver); `ConsentSensitiveDetails` holds the medical/dietary-SEN/emergency-contact fields (restricted, see [access-control.md](access-control.md)). This split exists because EF Core query filters are row-level, not column-level — see `docs/adr/0004-sensitive-data-access-control.md` for the full story of why the original single-entity design was wrong.
- **`Student.AppUserId` and `ParentGuardian.AppUserId`** (both nullable `int?`) were added during the authentication/authorization work, not in the original design gate package — they support self-login (Student) and parent-login (ParentGuardian). Documented in `docs/design/data-design.md`'s entity table; see [authentication-authorization.md](authentication-authorization.md).

All entity primary keys are application-assigned (`ValueGeneratedNever` in `VlmsDbContext`), except `SensitiveDataAccessLog.Id` which is database-generated (`ValueGeneratedOnAdd`) — see [data-access.md](data-access.md) for why.

## `Role` (`src/Vlms.Domain/Role.cs`)

Enum: `Admin`, `Teacher`, `Approver`, `Parent`, `Student`, `SafeguardingOfficer`. A single `AppUser` may hold more than one `UserRole` row (e.g. Teacher + Approver). **The `Approver` role is curriculum-approval only** — it has no consent/DBS/safeguarding privileges; do not add any authorization check that grants `Approver` access to `ConsentSensitiveDetails`, `DbsCheck`, or consent approval. This was an explicit discovery-phase correction (`docs/requirements/stakeholders.md`), not an oversight if you're tempted to "simplify" it.

## `ICurrentUserContext` (`src/Vlms.Domain/ICurrentUserContext.cs`)

```csharp
public interface ICurrentUserContext
{
    int? UserId { get; }
    bool HasRole(Role role);
}
```

Consumed by `VlmsDbContext`'s query filters and by `SensitiveDataAuditInterceptor` (both in `Vlms.Infrastructure`). Two implementations exist, both in Infrastructure — see [data-access.md](data-access.md) and [authentication-authorization.md](authentication-authorization.md). **Note:** the XML doc comment on this interface still says the Entra-backed implementation is "a later STATE.md item" — that's stale now (`EntraCurrentUserContext` exists); harmless but worth fixing next time this file is touched.
