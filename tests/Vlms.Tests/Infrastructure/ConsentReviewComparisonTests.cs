using Vlms.Domain;
using Vlms.Infrastructure.Safeguarding;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>TC-031: the consent review marks differences as "Changed" (functional.md FR-015).</summary>
public sealed class ConsentReviewComparisonTests
{
    private static readonly DateOnly Dob = new(2015, 3, 4);

    private static Student MakeStudent() => new()
    {
        Id = 1, Name = "Alex Smith", DateOfBirth = Dob, CurrentRankId = 1,
        Status = StudentStatus.Active, EnrolmentDate = new DateOnly(2024, 1, 1),
    };

    private static ConsentRecord MakeRecord(int id, string? name, DateOnly? dob, ConsentStatus status = ConsentStatus.Pending,
        bool photo = true, DateOnly? expiry = null) => new()
    {
        Id = id, StudentId = 1, PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 12, 31),
        PhotoMediaConsent = photo, TransportOffsiteConsent = true, DataSharingConsent = true, Status = status,
        SubmittedByParentId = 1, ExpiryDate = expiry ?? new DateOnly(2026, 12, 31), StudentName = name, StudentDateOfBirth = dob,
    };

    [Fact]
    public void MatchingNameAndDateOfBirth_AreNotChanged()
    {
        var fields = ConsentReviewComparison.CompareStudentDetails(MakeRecord(1, "Alex Smith", Dob), MakeStudent());
        Assert.All(fields, f => Assert.False(f.Changed));
    }

    [Fact]
    public void ChangedName_IsMarkedWithOldAndNewValues()
    {
        var fields = ConsentReviewComparison.CompareStudentDetails(MakeRecord(1, "Alex Smyth", Dob), MakeStudent());
        var name = Assert.Single(fields, f => f.Label == "Name");
        Assert.True(name.Changed);
        Assert.Equal("Alex Smith", name.Current);
        Assert.Equal("Alex Smyth", name.Submitted);
        Assert.False(fields.Single(f => f.Label == "Date of birth").Changed);
    }

    [Fact]
    public void ChangedDateOfBirth_IsMarkedWithOldAndNewValues()
    {
        var fields = ConsentReviewComparison.CompareStudentDetails(MakeRecord(1, "Alex Smith", new DateOnly(2015, 4, 3)), MakeStudent());
        var dob = Assert.Single(fields, f => f.Label == "Date of birth");
        Assert.True(dob.Changed);
        Assert.Equal("4 March 2015", dob.Current);
        Assert.Equal("3 April 2015", dob.Submitted);
        Assert.False(fields.Single(f => f.Label == "Name").Changed);
    }

    [Fact]
    public void FormWithoutPersonalDetails_IsNotAChange()
    {
        var fields = ConsentReviewComparison.CompareStudentDetails(MakeRecord(1, null, null), MakeStudent());
        Assert.All(fields, f => Assert.False(f.Changed));
    }

    [Fact]
    public void WithNoConsentInForce_NothingToCompare()
    {
        var result = ConsentReviewComparison.CompareWithInForce(MakeRecord(2, null, null), null, null, null);
        Assert.Empty(result);
    }

    [Fact]
    public void UpdateAgainstInForce_MarksOnlyTheFieldsThatDiffer()
    {
        var inForce = MakeRecord(1, null, null, ConsentStatus.Approved, photo: true);
        var update = MakeRecord(2, null, null, photo: false);
        var result = ConsentReviewComparison.CompareWithInForce(update, null, inForce, null);
        Assert.Equal(["Photo/media consent"], result.Where(f => f.Changed).Select(f => f.Label));
    }

    [Fact]
    public void FindInForce_IgnoresPendingRejectedAndExpired()
    {
        var today = new DateOnly(2026, 6, 1);
        var approved = MakeRecord(1, null, null, ConsentStatus.Approved);
        var records = new[]
        {
            approved,
            MakeRecord(2, null, null, ConsentStatus.Pending),
            MakeRecord(3, null, null, ConsentStatus.Rejected),
            MakeRecord(4, null, null, ConsentStatus.Approved, expiry: new DateOnly(2026, 1, 1)),
        };
        Assert.Same(approved, ConsentReviewComparison.FindInForce(records, 1, today));
    }
}
