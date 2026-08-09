using LyfStack.Agent.Windows.Sync;

namespace LyfStack.Agent.Windows.Tests;

public class SyncRangeQueryTests
{
    [Theory]
    [InlineData(null, SyncRangeKind.SinceLast)]
    [InlineData("since_last", SyncRangeKind.SinceLast)]
    [InlineData("week", SyncRangeKind.Week)]
    [InlineData("monthly", SyncRangeKind.Month)]
    [InlineData("all_time", SyncRangeKind.All)]
    public void Parse_range_aliases(string? range, SyncRangeKind expected)
    {
        Assert.Equal(expected, SyncRangeQuery.Parse(range).Range);
    }

    [Fact]
    public void Custom_from_to_dates()
    {
        SyncRangeQuery q = SyncRangeQuery.Parse("custom", "2026-01-01", "2026-01-31");
        Assert.Equal(SyncRangeKind.Custom, q.Range);
        ResolvedSyncWindow w = q.Resolve(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.False(w.PendingOnly);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), w.From);
        Assert.Equal(new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero), w.To);
    }

    [Fact]
    public void Query_string_includes_range()
    {
        string qs = SyncRangeQuery.Parse("week").ToQueryString(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains("range=week", qs);
        Assert.Contains("from=", qs);
        Assert.Contains("to=", qs);
    }

    [Fact]
    public void BuildRequestUrl_appends_query()
    {
        string url = SyncPayloadFactory.BuildRequestUrl(
            "https://api.lyfstack.app/api/v1/device-activity/sync",
            SyncRangeQuery.SinceLast);

        Assert.Equal(
            "https://api.lyfstack.app/api/v1/device-activity/sync?range=since_last",
            url);
    }
}
