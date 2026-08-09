using System.ComponentModel;
using System.Runtime.CompilerServices;
using LyfStack.Agent.Windows.Models;

namespace LyfStack.Agent.Windows.UI;

public sealed class SessionRow : INotifyPropertyChanged
{
    public Guid Id { get; init; }
    public string Application { get; init; } = "";
    public string Process { get; init; } = "";
    public string Started { get; init; } = "";
    public string Ended { get; init; } = "";
    public string Active { get; init; } = "";
    public string Idle { get; init; } = "";
    public string State { get; init; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Category { get; init; } = "Other";

    public static SessionRow FromSession(UsageSession session, string category = "Other")
    {
        return new SessionRow
        {
            Id = session.Id,
            Application = session.ApplicationName,
            Process = session.ProcessName,
            Category = category,
            Started = session.StartedAt.ToLocalTime().ToString("MMM d  HH:mm"),
            Ended = session.EndedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "Open",
            Active = Format(session.ActiveDuration),
            Idle = Format(session.IdleDuration),
            State = session.LastState.ToString()
        };
    }

    private static string Format(TimeSpan value) =>
        value < TimeSpan.FromHours(1)
            ? value.ToString(@"mm\:ss")
            : value.ToString(@"h\:mm\:ss");

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
