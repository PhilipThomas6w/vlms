using Vlms.Infrastructure.Chapters;
using Xunit;

namespace Vlms.Tests.Infrastructure;

/// <summary>
/// The national totals shown above the per-chapter figures (FR-021, Philip 27/10/2026): sums of the
/// per-chapter counts, with the attendance rate worked out from the summed marks.
/// </summary>
public sealed class NationalCollatedTests
{
    private static ChapterTotals Chapter(int id, int[] studentsPerRank, params ChapterMonthAttendance[] attendance)
    {
        var byRank = studentsPerRank.Select((n, i) => new ChapterRankTotal(i + 1, $"Rank {i + 1}", i + 1, n)).ToList();
        return new ChapterTotals(id, $"Chapter {id}", true, byRank.Sum(r => r.Students), byRank, attendance);
    }

    [Fact]
    public void StudentsOnRoll_AreSummedPerRankAcrossChapters_AndInTotal()
    {
        var national = NationalCollated.From([Chapter(1, [3, 0, 2]), Chapter(2, [4, 1, 0])]);

        Assert.Equal([7, 1, 2], national.ByRank.Select(r => r.Students));
        Assert.Equal([1, 2, 3], national.ByRank.Select(r => r.RankOrder));
        Assert.Equal(10, national.StudentsOnRoll);
    }

    [Fact]
    public void Attendance_IsSummedPerMonth_NewestFirst_AndTheRateComesFromTheSums()
    {
        // October: 1 of 10 plus 9 of 10 present. September: 20 of 20 plus 0 of 2, where the mean of
        // the chapters' rates (100% and 0%) would be 50% but the sums give 91%.
        var national = NationalCollated.From(
        [
            Chapter(1, [1], new ChapterMonthAttendance(2026, 10, 1, 10, 1), new ChapterMonthAttendance(2026, 9, 2, 20, 20)),
            Chapter(2, [1], new ChapterMonthAttendance(2026, 10, 2, 10, 9), new ChapterMonthAttendance(2026, 9, 1, 2, 0)),
        ]);

        Assert.Equal([(2026, 10), (2026, 9)], national.Attendance.Select(m => (m.Year, m.Month)));

        var october = national.Attendance[0];
        Assert.Equal((3, 20, 10, 50), (october.Meetings, october.Marks, october.Present, october.RatePercent));

        var september = national.Attendance[1];
        Assert.Equal((3, 22, 20, 91), (september.Meetings, september.Marks, september.Present, september.RatePercent));
    }

    [Fact]
    public void NoChapters_GivesEmptyFigures()
    {
        var national = NationalCollated.From([]);

        Assert.Equal(0, national.StudentsOnRoll);
        Assert.Empty(national.ByRank);
        Assert.Empty(national.Attendance);
    }

    [Fact]
    public void AMonthWithMeetingsButNoMarks_HasNoRate()
    {
        var national = NationalCollated.From([Chapter(1, [1], new ChapterMonthAttendance(2026, 10, 1, 0, 0))]);

        Assert.Null(national.Attendance.Single().RatePercent);
    }
}
