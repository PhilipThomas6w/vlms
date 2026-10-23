using Vlms.Domain;
using Vlms.Infrastructure.Safeguarding;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The all-records list on /safeguarding/consent says which approved record is in force (TC-041). It must use
/// the same rule the rest of the app uses to pick the consent in force: <see cref="ConsentReviewComparison.FindInForce"/>.
/// Also the reader-friendly role names used on the staff and role pages.
/// </summary>
public sealed class ConsentStatusLabelTests
{
    private static readonly DateOnly Today = new(2026, 10, 23);

    private static ConsentRecord Record(int id, int studentId, ConsentStatus status, DateOnly expiry, DateTime? decidedAt) => new()
    {
        Id = id,
        StudentId = studentId,
        PeriodStart = new DateOnly(2025, 9, 1),
        PeriodEnd = expiry,
        PhotoMediaConsent = true,
        TransportConsent = true,
        OffsiteTripConsent = true,
        DataSharingConsent = true,
        Status = status,
        SubmittedByParentId = 1,
        ExpiryDate = expiry,
        DecidedAt = decidedAt,
    };

    [Fact]
    public void Two_approved_records_for_one_student_show_the_latest_in_force_and_the_earlier_replaced()
    {
        var older = Record(1, 7, ConsentStatus.Approved, new DateOnly(2027, 8, 31), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Record(2, 7, ConsentStatus.Approved, new DateOnly(2027, 8, 31), new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
        var records = new[] { older, newer };

        Assert.Equal("Approved (in force)", ConsentReviewComparison.StatusLabel(newer, records, Today));
        Assert.Equal("Approved (replaced)", ConsentReviewComparison.StatusLabel(older, records, Today));
    }

    [Fact]
    public void Another_students_approved_record_does_not_replace_this_one()
    {
        var mine = Record(1, 7, ConsentStatus.Approved, new DateOnly(2027, 8, 31), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var theirs = Record(2, 8, ConsentStatus.Approved, new DateOnly(2027, 8, 31), new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("Approved (in force)", ConsentReviewComparison.StatusLabel(mine, [mine, theirs], Today));
    }

    [Fact]
    public void An_approved_record_with_nothing_in_force_is_expired_and_other_statuses_print_as_they_are()
    {
        var expired = Record(1, 7, ConsentStatus.Approved, new DateOnly(2026, 8, 31), new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var pending = Record(2, 7, ConsentStatus.Pending, new DateOnly(2027, 8, 31), null);
        var rejected = Record(3, 7, ConsentStatus.Rejected, new DateOnly(2027, 8, 31), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var records = new[] { expired, pending, rejected };

        Assert.Equal("Approved (expired)", ConsentReviewComparison.StatusLabel(expired, records, Today));
        Assert.Equal("Pending", ConsentReviewComparison.StatusLabel(pending, records, Today));
        Assert.Equal("Rejected", ConsentReviewComparison.StatusLabel(rejected, records, Today));
    }

    [Theory]
    [InlineData(Role.SafeguardingOfficer, "Safeguarding Officer")]
    [InlineData(Role.Admin, "Admin")]
    [InlineData(Role.Teacher, "Teacher")]
    public void Roles_show_with_spaces_between_words(Role role, string expected) =>
        Assert.Equal(expected, role.DisplayName());
}
