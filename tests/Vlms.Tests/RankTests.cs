using Vlms.Domain;
using Xunit;

namespace Vlms.Tests;

public class RankTests
{
    [Fact]
    public void IsBefore_ReturnsTrue_WhenOrderIsLower()
    {
        var recruit = new Rank { Id = 1, Order = 1, Code = "R1", Name = "Recruit" };
        var guardsman = new Rank { Id = 2, Order = 2, Code = "R2", Name = "Guardsman" };

        Assert.True(recruit.IsBefore(guardsman));
        Assert.False(guardsman.IsBefore(recruit));
    }
}
