namespace Vlms.Domain;

/// <summary>What someone said they are joining a chapter as when they registered. Grants nothing.</summary>
public enum JoiningAs
{
    Parent,
    Teacher,
    Other
}

/// <summary>Display text for <see cref="JoiningAs"/>.</summary>
public static class JoiningAsText
{
    public static string DisplayName(this JoiningAs? joiningAs) => joiningAs switch
    {
        JoiningAs.Parent => "Parent",
        JoiningAs.Teacher => "Teacher",
        JoiningAs.Other => "Other",
        _ => "Not asked"
    };
}

/// <summary>
/// Someone who has registered and asked to join a chapter (functional.md FR-021 as amended
/// 09/11/2026). Created by the Register page, or by a National Administrator placing an account
/// that registered before chapters were asked for. While it is open (<see cref="DismissedAt"/> null)
/// and the person holds no role in <see cref="ChapterId"/>, they are on that chapter's "New
/// registrations" list. Granting them a role there takes them off it; an Admin may instead dismiss
/// it, which deletes nothing. Grants no role and no access of its own.
///
/// National table, like <see cref="StaffStepDown"/>: no chapter query filter, because a National
/// Administrator must see whether an account is on any chapter's list. The service reads the
/// active chapter's rows only for Admins.
/// </summary>
public sealed class ChapterJoinRequest
{
    public int Id { get; init; }

    /// <summary>The person registering. Their rows go with their account.</summary>
    public required int UserId { get; init; }

    public required int ChapterId { get; init; }

    /// <summary>What they said they are joining as; null when placed by a National Administrator (not asked).</summary>
    public JoiningAs? JoiningAs { get; init; }

    public required DateTime RequestedAt { get; init; }

    /// <summary>The National Administrator who placed the account on this chapter's list; null when the person chose it.</summary>
    public int? PlacedByUserId { get; init; }

    public DateTime? DismissedAt { get; set; }

    /// <summary>The Admin who dismissed it; null once that Admin's account has been deleted (docs/adr/0014).</summary>
    public int? DismissedByUserId { get; set; }
}
