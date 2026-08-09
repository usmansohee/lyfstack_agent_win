using System.Text;
using System.Text.Json;
using LyfStack.Agent.Windows.Configuration;
using LyfStack.Agent.Windows.Models;

namespace LyfStack.Agent.Windows.Services;

public static class SessionExportService
{
    public static string ToCsv(IEnumerable<UsageSession> sessions, IEnumerable<CategoryRule>? rules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Application,Process,Category,StartedAt,EndedAt,ActiveSeconds,IdleSeconds,State");
        foreach (UsageSession session in sessions)
        {
            string category = CategoryResolver.Resolve(session.ProcessName, rules, session.ExecutablePath, session.ApplicationName);
            sb.Append(Escape(session.Id.ToString("D"))).Append(',');
            sb.Append(Escape(session.ApplicationName)).Append(',');
            sb.Append(Escape(session.ProcessName)).Append(',');
            sb.Append(Escape(category)).Append(',');
            sb.Append(Escape(session.StartedAt.ToLocalTime().ToString("O"))).Append(',');
            sb.Append(Escape(session.EndedAt?.ToLocalTime().ToString("O") ?? "")).Append(',');
            sb.Append((int)session.ActiveDuration.TotalSeconds).Append(',');
            sb.Append((int)session.IdleDuration.TotalSeconds).Append(',');
            sb.AppendLine(Escape(session.LastState.ToString()));
        }

        return sb.ToString();
    }

    public static string ToJson(IEnumerable<UsageSession> sessions, IEnumerable<CategoryRule>? rules)
    {
        var payload = sessions.Select(s => new
        {
            id = s.Id,
            applicationName = s.ApplicationName,
            processName = s.ProcessName,
            category = CategoryResolver.Resolve(s.ProcessName, rules, s.ExecutablePath, s.ApplicationName),
            startedAt = s.StartedAt,
            endedAt = s.EndedAt,
            activeDurationSeconds = (int)s.ActiveDuration.TotalSeconds,
            idleDurationSeconds = (int)s.IdleDuration.TotalSeconds,
            lastState = s.LastState.ToString(),
            isOpen = s.IsOpen
        });

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}

public sealed class ActivitySummary
{
    public TimeSpan TotalActive { get; init; }
    public TimeSpan TotalIdle { get; init; }
    public TimeSpan TotalTracked => TotalActive + TotalIdle;
    public int SessionCount { get; init; }
    public int UniqueApps { get; init; }
    public double ActivePercent { get; init; }
    public IReadOnlyList<AppUsageStat> TopApps { get; init; } = Array.Empty<AppUsageStat>();
    public IReadOnlyList<CategoryUsageStat> ByCategory { get; init; } = Array.Empty<CategoryUsageStat>();
}

public sealed class AppUsageStat
{
    public required string ApplicationName { get; init; }
    public required string ProcessName { get; init; }
    public required string Category { get; init; }
    public TimeSpan Active { get; init; }
    public TimeSpan Idle { get; init; }
}

public sealed class CategoryUsageStat
{
    public required string Category { get; init; }
    public TimeSpan Active { get; init; }
    public int SessionCount { get; init; }
}

public static class ActivitySummaryBuilder
{
    public static ActivitySummary Build(
        IEnumerable<UsageSession> sessions,
        IEnumerable<CategoryRule>? rules,
        int topN = 6)
    {
        var list = sessions.ToList();
        double totalActiveMs = list.Sum(s => s.ActiveDuration.TotalMilliseconds);
        double totalIdleMs = list.Sum(s => s.IdleDuration.TotalMilliseconds);
        double tracked = totalActiveMs + totalIdleMs;

        var grouped = list
            .GroupBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppUsageStat
            {
                ProcessName = g.Key,
                ApplicationName = g.First().ApplicationName,
                Category = CategoryResolver.Resolve(g.Key, rules, g.First().ExecutablePath, g.First().ApplicationName),
                Active = TimeSpan.FromMilliseconds(g.Sum(x => x.ActiveDuration.TotalMilliseconds)),
                Idle = TimeSpan.FromMilliseconds(g.Sum(x => x.IdleDuration.TotalMilliseconds))
            })
            .OrderByDescending(x => x.Active)
            .Take(topN)
            .ToList();

        var byCategory = list
            .GroupBy(s => CategoryResolver.Resolve(s.ProcessName, rules, s.ExecutablePath, s.ApplicationName), StringComparer.OrdinalIgnoreCase)
            .Select(g => new CategoryUsageStat
            {
                Category = g.Key,
                Active = TimeSpan.FromMilliseconds(g.Sum(x => x.ActiveDuration.TotalMilliseconds)),
                SessionCount = g.Count()
            })
            .OrderByDescending(x => x.Active)
            .ToList();

        return new ActivitySummary
        {
            TotalActive = TimeSpan.FromMilliseconds(totalActiveMs),
            TotalIdle = TimeSpan.FromMilliseconds(totalIdleMs),
            SessionCount = list.Count,
            UniqueApps = list.Select(s => s.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ActivePercent = tracked <= 0 ? 0 : (totalActiveMs / tracked) * 100.0,
            TopApps = grouped,
            ByCategory = byCategory
        };
    }
}
