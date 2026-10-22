using Vlms.Infrastructure.Safeguarding;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The church consent year rule (adr/0008 answer 2, FR-001): consent ends on the 31 August that
/// closes the church year (1 September to 31 August) containing the submission date, no cut-off.
/// </summary>
public sealed class ConsentYearTests
{
    [Theory]
    [InlineData(2027, 7, 1, 2027, 8, 31)]    // the brief's example
    [InlineData(2027, 9, 1, 2028, 8, 31)]    // first day of the next year
    [InlineData(2027, 8, 31, 2027, 8, 31)]   // last day of the year: expires the same day
    [InlineData(2027, 8, 30, 2027, 8, 31)]
    [InlineData(2027, 12, 31, 2028, 8, 31)]
    [InlineData(2028, 1, 1, 2028, 8, 31)]
    [InlineData(2028, 2, 29, 2028, 8, 31)]   // leap day
    [InlineData(2027, 2, 28, 2027, 8, 31)]
    [InlineData(2028, 9, 1, 2029, 8, 31)]
    public void EndFor_IsThe31AugustClosingTheChurchYear(int y, int m, int d, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), ConsentYear.EndFor(new DateOnly(y, m, d)));
}
