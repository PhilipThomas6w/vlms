namespace Vlms.Domain;

/// <summary>
/// The chapter a signed-in person last chose to work in with the chapter switcher
/// (docs/adr/0010-chapters.md, "Active chapter"). Only a preference: it is checked against the
/// person's current role rows on every check by <c>IdentityCurrentUserContext</c>, and ignored if
/// they no longer hold a role in that chapter. Kept in its own table rather than on
/// <see cref="AppUser"/>, whose updates copy every property with <c>SetValues</c>.
/// </summary>
public sealed class ActiveChapterChoice
{
    public required int UserId { get; init; }
    public required int ChapterId { get; init; }
}
