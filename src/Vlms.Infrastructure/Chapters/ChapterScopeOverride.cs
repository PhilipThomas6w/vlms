namespace Vlms.Infrastructure.Chapters;

/// <summary>
/// The one sanctioned way to read a chapter's rows without working in that chapter
/// (docs/adr/0010-chapters.md): someone accepting a guardian invite is usually not signed in, so
/// they have no active chapter and the chapter query filters would hide the invite and the guardian
/// record it is for. <c>GuardianInviteService</c>, and nothing else, enters this scope, and only
/// after the invite token has been verified (Data Protection signature and expiry) and for the
/// chapter recorded on that invite. While entered, <c>IdentityCurrentUserContext.ActiveChapterId</c>
/// is that chapter; roles are unchanged, so an anonymous caller still holds none and the
/// sensitive-data filters still show them nothing. One per DI scope; <c>build/check-access-control.ps1</c>
/// fails the gate if any other file calls <see cref="EnterForVerifiedInvite"/>.
/// </summary>
public sealed class ChapterScopeOverride
{
    public int? ChapterId { get; private set; }

    /// <summary>Enters the invite's chapter until the returned scope is disposed.</summary>
    public IDisposable EnterForVerifiedInvite(int chapterId)
    {
        if (ChapterId is not null)
        {
            throw new InvalidOperationException("A chapter scope is already entered.");
        }

        ChapterId = chapterId;
        return new Exit(this);
    }

    private sealed class Exit(ChapterScopeOverride owner) : IDisposable
    {
        public void Dispose() => owner.ChapterId = null;
    }
}
