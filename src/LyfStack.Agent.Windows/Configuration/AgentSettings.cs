using System.Text.Json;

namespace LyfStack.Agent.Windows.Configuration;

public sealed class AgentSettings
{
    public bool AutoSyncEnabled { get; set; } = true;

    public int AutoSyncIntervalMinutes { get; set; } = 15;

    public string SyncEndpointUrl { get; set; } =
        "https://webhook.site/162a6652-aec2-4bcc-ad8d-d3a4acb63181";

    /// <summary>
    /// Outbound WebSocket so LyfStack website can send SYNC_NOW / PAUSE without a public PC URL.
    /// </summary>
    public bool DeviceConnectionEnabled { get; set; }

    public string DeviceConnectionUrl { get; set; } =
        "wss://api.lyfstack.app/device-connection";

    public string DeviceConnectionToken { get; set; } = "";

    public List<string> IgnoredProcesses { get; set; } = ProcessIgnore.DefaultIgnored.ToList();

    public List<CategoryRule> CategoryRules { get; set; } = new();
}

public static class AgentSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AgentSettings Load()
    {
        try
        {
            if (!File.Exists(AgentPaths.SettingsPath))
            {
                var defaults = new AgentSettings();
                Save(defaults);
                return defaults;
            }

            string json = File.ReadAllText(AgentPaths.SettingsPath);
            AgentSettings? settings = JsonSerializer.Deserialize<AgentSettings>(json, JsonOptions);
            if (settings is null)
            {
                return new AgentSettings();
            }

            if (settings.AutoSyncIntervalMinutes is < 5 or > 120)
            {
                settings.AutoSyncIntervalMinutes = 15;
            }

            if (string.IsNullOrWhiteSpace(settings.SyncEndpointUrl))
            {
                settings.SyncEndpointUrl = new AgentSettings().SyncEndpointUrl;
            }

            if (string.IsNullOrWhiteSpace(settings.DeviceConnectionUrl))
            {
                settings.DeviceConnectionUrl = new AgentSettings().DeviceConnectionUrl;
            }

            settings.DeviceConnectionToken ??= "";
            settings.IgnoredProcesses ??= ProcessIgnore.DefaultIgnored.ToList();
            settings.CategoryRules ??= new List<CategoryRule>();

            return settings;
        }
        catch
        {
            return new AgentSettings();
        }
    }

    public static void Save(AgentSettings settings)
    {
        AgentPaths.EnsureDataDirectory();
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(AgentPaths.SettingsPath, json);
    }
}
