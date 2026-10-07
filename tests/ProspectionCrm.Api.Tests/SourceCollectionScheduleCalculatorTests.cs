using ProspectionCrm.Api.Services.Collection;
using Xunit;

namespace ProspectionCrm.Api.Tests;

public sealed class SourceCollectionScheduleCalculatorTests
{
    [Theory]
    [InlineData("2026-10-07T09:29:59Z", 570, "2026-10-07T09:30:00Z")]
    [InlineData("2026-10-07T09:30:00Z", 570, "2026-10-08T09:30:00Z")]
    [InlineData("2026-10-07T09:30:00.001Z", 570, "2026-10-08T09:30:00Z")]
    [InlineData("2026-12-31T23:59:59Z", 0, "2027-01-01T00:00:00Z")]
    [InlineData("2028-02-28T23:59:59Z", 0, "2028-02-29T00:00:00Z")]
    [InlineData("2026-10-08T00:30:00+02:00", 1380, "2026-10-07T23:00:00Z")]
    public void FirstStrictlyFutureUtcMinute(string value, int minute, string expected)
    {
        var now = DateTimeOffset.Parse(value);
        var next = SourceCollectionScheduleCalculator.Next(now, minute);
        Assert.Equal(DateTimeOffset.Parse(expected), next);
        Assert.Equal(TimeSpan.Zero, next.Offset);
        Assert.True(next > now);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("9:30")] [InlineData("24:00")]
    [InlineData("12:60")] [InlineData("12:30:00")] [InlineData(" 12:30")] [InlineData("12:30Z")]
    public void InvalidTimeIsRejected(string? value) => Assert.False(SourceCollectionScheduleCalculator.TryParse(value, out _));

    [Theory]
    [InlineData("00:00", 0)] [InlineData("09:30", 570)] [InlineData("23:59", 1439)]
    public void ValidTimeRoundTrips(string value, int expected)
    {
        Assert.True(SourceCollectionScheduleCalculator.TryParse(value, out var minute));
        Assert.Equal(expected, minute); Assert.Equal(value, SourceCollectionScheduleCalculator.Format(minute));
    }
}
