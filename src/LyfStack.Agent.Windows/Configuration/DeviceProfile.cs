using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LyfStack.Agent.Windows.Configuration;

public sealed class DeviceProfile
{
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    public DateTimeOffset FirstInstalledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FirstSyncedAt { get; set; }
}

public sealed class DeviceInfoSnapshot
{
    public required Guid DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string UserName { get; init; }
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }
    public required string OsCaption { get; init; }
    public required string OsBuild { get; init; }
    public required string Architecture { get; init; }
    public required string CpuName { get; init; }
    public required string RamGb { get; init; }
    public required string GpuName { get; init; }
    public required string AgentVersion { get; init; }
    public required DateTimeOffset FirstInstalledAt { get; init; }
    public DateTimeOffset? FirstSyncedAt { get; init; }
    public DateTimeOffset? LastSyncedAt { get; init; }
    public required string SyncEndpoint { get; init; }
}

public static class DeviceProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly object HardwareLock = new();
    private static HardwareInfo? _hardwareCache;

    public static string ProfilePath => Path.Combine(AgentPaths.DataDirectory, "device.json");

    public static DeviceProfile LoadOrCreate()
    {
        AgentPaths.EnsureDataDirectory();
        try
        {
            if (File.Exists(ProfilePath))
            {
                string json = File.ReadAllText(ProfilePath);
                DeviceProfile? profile = JsonSerializer.Deserialize<DeviceProfile>(json, JsonOptions);
                if (profile is not null && profile.DeviceId != Guid.Empty)
                {
                    return profile;
                }
            }
        }
        catch
        {
        }

        var created = new DeviceProfile();
        Save(created);
        return created;
    }

    public static void Save(DeviceProfile profile)
    {
        AgentPaths.EnsureDataDirectory();
        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(profile, JsonOptions));
    }

    public static void MarkFirstSyncIfNeeded(DateTimeOffset syncedAt)
    {
        DeviceProfile profile = LoadOrCreate();
        if (profile.FirstSyncedAt is null)
        {
            profile.FirstSyncedAt = syncedAt;
            Save(profile);
        }
    }

    public static DeviceInfoSnapshot Capture(AgentSettings settings, DateTimeOffset? lastSyncedAt)
    {
        DeviceProfile profile = LoadOrCreate();
        HardwareInfo hw = GetHardwareCached();
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        return new DeviceInfoSnapshot
        {
            DeviceId = profile.DeviceId,
            DeviceName = Environment.MachineName,
            UserName = $"{Environment.UserDomainName}\\{Environment.UserName}",
            Manufacturer = hw.Manufacturer,
            Model = hw.Model,
            OsCaption = hw.OsCaption,
            OsBuild = hw.OsBuild,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            CpuName = hw.CpuName,
            RamGb = hw.RamGb,
            GpuName = hw.GpuName,
            AgentVersion = version,
            FirstInstalledAt = profile.FirstInstalledAt,
            FirstSyncedAt = profile.FirstSyncedAt,
            LastSyncedAt = lastSyncedAt,
            SyncEndpoint = settings.SyncEndpointUrl
        };
    }

    private static HardwareInfo GetHardwareCached()
    {
        lock (HardwareLock)
        {
            return _hardwareCache ??= ReadHardware();
        }
    }

    private static HardwareInfo ReadHardware()
    {
        try
        {
            string manufacturer = "Unknown";
            string model = "Unknown";
            string ramGb = "Unknown";
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject obj in results)
                {
                    using (obj)
                    {
                        manufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? "Unknown";
                        model = obj["Model"]?.ToString()?.Trim() ?? "Unknown";
                        if (ulong.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out ulong bytes) && bytes > 0)
                        {
                            ramGb = $"{Math.Round(bytes / (1024d * 1024d * 1024d), 1)} GB";
                        }
                    }

                    break;
                }
            }

            string cpu = QueryFirst("SELECT Name FROM Win32_Processor", r => r["Name"]?.ToString()) ?? "Unknown";
            string osCaption = QueryFirst("SELECT Caption FROM Win32_OperatingSystem", r => r["Caption"]?.ToString())
                ?? RuntimeInformation.OSDescription;
            string osBuild = QueryFirst(
                "SELECT Version, BuildNumber FROM Win32_OperatingSystem",
                r =>
                {
                    string version = r["Version"]?.ToString() ?? "";
                    string build = r["BuildNumber"]?.ToString() ?? "";
                    return string.IsNullOrWhiteSpace(build) ? version : $"{version} (build {build})";
                }) ?? "";
            string gpu = QueryFirst("SELECT Name FROM Win32_VideoController", r => r["Name"]?.ToString()) ?? "Unknown";

            return new HardwareInfo(
                manufacturer.Trim(),
                model.Trim(),
                osCaption.Trim(),
                osBuild.Trim(),
                cpu.Trim(),
                ramGb,
                gpu.Trim());
        }
        catch
        {
            return new HardwareInfo(
                "Unknown",
                "Unknown",
                RuntimeInformation.OSDescription,
                "",
                "Unknown",
                "Unknown",
                "Unknown");
        }
    }

    private static string? QueryFirst(string wql, Func<ManagementBaseObject, string?> selector)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementBaseObject obj in results)
        {
            using (obj)
            {
                string? value = selector(obj);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private sealed record HardwareInfo(
        string Manufacturer,
        string Model,
        string OsCaption,
        string OsBuild,
        string CpuName,
        string RamGb,
        string GpuName);
}
