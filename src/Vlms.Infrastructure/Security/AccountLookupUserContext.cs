using Vlms.Domain;

namespace Vlms.Infrastructure.Security;

/// <summary>
/// An <see cref="ICurrentUserContext"/> that is one account and nothing more: its <see cref="UserId"/>,
/// no roles, no active chapter, no MFA. For identity checks that run before anyone is signed in
/// (<see cref="Identity.VlmsUserConfirmation"/>: "is this sign-in a student's?"), where the
/// anonymous caller would see no chapter's rows at all (docs/adr/0010-chapters.md). Through the
/// chapter filters' own-record arm it sees only that account's own student record, guardian rows
/// and children; through the sensitive-data filters it sees nothing. Never a request's runtime context.
/// </summary>
public sealed class AccountLookupUserContext : ICurrentUserContext
{
    public AccountLookupUserContext(int userId) => UserId = userId;

    public int? UserId { get; }

    public bool HasRole(Role role) => false;

    public bool HasMfaEnabled => false;

    public int? ActiveChapterId => null;

    public bool SeesAllChapters => false;
}
