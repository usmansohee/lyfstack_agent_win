using System.Globalization;
using System.Text;

namespace LyfStack.Agent.Windows.Sync;

/// <summary>
/// Sync window for agent → LyfStack API and website Sync now.
/// Default is incremental (since last successful sync).
/// </summary>
public enum SyncRangeKind
{
    SinceLast,
    Today,
    Week,
    Month,
    Year,
    All,
    Custom
}

public sealed class SyncRangeQuery
{
    public SyncRangeKind Range { get; init; } = SyncRangeKind.SinceLast;

    /// <summary>Inclusive start (UTC). Used for Custom, and echoed in query string when resolved.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Inclusive end (UTC). Defaults to now when omitted for Custom.</summary>
    public DateTimeOffset? To { get; init; }

    public static SyncRangeQuery SinceLast { get; } = new() { Range = SyncRangeKind.SinceLast };

    public static SyncRangeQuery Parse(string? range, string? from = null, string? to = null)
    {
        string key = (range ?? "since_last").Trim().ToLowerInvariant();
        SyncRangeKind kind = key switch
        {
            "since_last" or "last" or "pending" or "incremental" => SyncRangeKind.SinceLast,
            "today" or "day" => SyncRangeKind.Today,
            "week" or "weekly" => SyncRangeKind.Week,
            "month" or "monthly" => SyncRangeKind.Month,
            "year" or "yearly" => SyncRangeKind.Year,
            "all" or "all_time" or "alltime" => SyncRangeKind.All,
            "custom" or "range" => SyncRangeKind.Custom,
            _ => SyncRangeKind.SinceLast
        };

        DateTimeOffset? fromDto = TryParseDate(from);
        DateTimeOffset? toDto = TryParseDate(to);

        if (fromDto is not null || toDto is not null)
        {
            // Explicit dates imply custom window unless a preset range was chosen without dates.
            if (kind is SyncRangeKind.SinceLast && (fromDto is not null || toDto is not null))
            {
                kind = SyncRangeKind.Custom;
            }
        }

        if (kind == SyncRangeKind.Custom && fromDto is null && toDto is null)
        {
            kind = SyncRangeKind.SinceLast;
        }

        return new SyncRangeQuery
        {
            Range = kind,
            From = fromDto,
            To = toDto
        };
    }

    public ResolvedSyncWindow Resolve(DateTimeOffset? now = null)
    {
        DateTimeOffset utcNow = now ?? DateTimeOffset.UtcNow;
        DateTime localToday = utcNow.ToLocalTime().Date;

        return Range switch
        {
            SyncRangeKind.SinceLast => new ResolvedSyncWindow(Range, PendingOnly: true, From: null, To: null),
            SyncRangeKind.Today => new ResolvedSyncWindow(
                Range,
                PendingOnly: false,
                From: new DateTimeOffset(localToday),
                To: utcNow),
            SyncRangeKind.Week => new ResolvedSyncWindow(
                Range,
                PendingOnly: false,
                From: new DateTimeOffset(StartOfWeekMonday(localToday)),
                To: utcNow),
            SyncRangeKind.Month => new ResolvedSyncWindow(
                Range,
                PendingOnly: false,
                From: new DateTimeOffset(new DateTime(localToday.Year, localToday.Month, 1)),
                To: utcNow),
            SyncRangeKind.Year => new ResolvedSyncWindow(
                Range,
                PendingOnly: false,
                From: new DateTimeOffset(new DateTime(localToday.Year, 1, 1)),
                To: utcNow),
            SyncRangeKind.All => new ResolvedSyncWindow(Range, PendingOnly: false, From: null, To: null),
            SyncRangeKind.Custom => new ResolvedSyncWindow(
                Range,
                PendingOnly: false,
                From: From,
                To: To ?? utcNow),
            _ => new ResolvedSyncWindow(SyncRangeKind.SinceLast, PendingOnly: true, From: null, To: null)
        };
    }

    /// <summary>Builds <c>?range=...&amp;from=...&amp;to=...</c> for the LyfStack sync URL.</summary>
    public string ToQueryString(DateTimeOffset? now = null)
    {
        ResolvedSyncWindow window = Resolve(now);
        var sb = new StringBuilder();
        sb.Append("range=").Append(Uri.EscapeDataString(ToRangeParam(window.Range)));

        if (window.From is DateTimeOffset from)
        {
            sb.Append("&from=").Append(Uri.EscapeDataString(from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        }

        if (window.To is DateTimeOffset to)
        {
            sb.Append("&to=").Append(Uri.EscapeDataString(to.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }

    public string ToRangeParam() => ToRangeParam(Range);

    public static string ToRangeParam(SyncRangeKind range) => range switch
    {
        SyncRangeKind.SinceLast => "since_last",
        SyncRangeKind.Today => "today",
        SyncRangeKind.Week => "week",
        SyncRangeKind.Month => "month",
        SyncRangeKind.Year => "year",
        SyncRangeKind.All => "all",
        SyncRangeKind.Custom => "custom",
        _ => "since_last"
    };

    private static DateTime StartOfWeekMonday(DateTime date)
    {
        int diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-diff);
    }

    private static DateTimeOffset? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset dto))
        {
            return dto;
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime dt))
        {
            return new DateTimeOffset(dt);
        }

        return null;
    }
}

public readonly record struct ResolvedSyncWindow(
    SyncRangeKind Range,
    bool PendingOnly,
    DateTimeOffset? From,
    DateTimeOffset? To);
