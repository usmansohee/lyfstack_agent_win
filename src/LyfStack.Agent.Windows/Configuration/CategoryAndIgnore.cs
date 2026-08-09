namespace LyfStack.Agent.Windows.Configuration;

public sealed class CategoryRule
{
    public string ProcessName { get; set; } = "";
    public string Category { get; set; } = "Other";
}

public static class AppCategories
{
    public static readonly string[] All =
    [
        "Work",
        "Browser",
        "Games",
        "Entertainment",
        "Communication",
        "System",
        "Other"
    ];
}

/// <summary>
/// Resolves categories automatically. Manual rules are optional overrides only.
/// </summary>
public static class CategoryResolver
{
    public static string Resolve(
        string? processName,
        IEnumerable<CategoryRule>? rules,
        string? executablePath = null,
        string? applicationName = null)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return "Other";
        }

        // 1) Optional manual override
        foreach (CategoryRule rule in rules ?? Array.Empty<CategoryRule>())
        {
            if (string.IsNullOrWhiteSpace(rule.ProcessName))
            {
                continue;
            }

            if (NamesMatch(processName, rule.ProcessName))
            {
                return string.IsNullOrWhiteSpace(rule.Category) ? "Other" : rule.Category;
            }
        }

        // 2) Automatic detection
        return AutoCategoryDetector.Detect(processName, executablePath, applicationName);
    }

    private static bool NamesMatch(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe";
}

public static class AutoCategoryDetector
{
    private static readonly string[] GamePathMarkers =
    [
        @"\steamapps\common\",
        @"\steamapps\workshop\",
        @"\epic games\",
        @"\xboxgames\",
        @"\gog galaxy\games\",
        @"\riot games\",
        @"\battle.net\",
        @"\origin games\",
        @"\ea games\",
        @"\ubisoft\ubiquitous\",
        @"\ubisoft game launcher\games\",
        @"\roblox\",
        @"\minecraft\",
        @"\playstation\",
        @"\rockstar games\",
        @"\legendary\",
        @"\heroic\prefixes\",
        @"\games\"
    ];

    /// <summary>
    /// Files commonly shipped next to game exes (Unity/Unreal/Steamworks stubs, etc.),
    /// including many direct / offline installs.
    /// </summary>
    private static readonly string[] GameSiblingFiles =
    [
        "UnityPlayer.dll",
        "GameAssembly.dll",
        "steam_api64.dll",
        "steam_api.dll",
        "EOSSDK-Win64-Shipping.dll",
        "Galaxy64.dll",
        "Galaxy.dll"
    ];

    private static readonly Dictionary<string, bool> LocalGameDirCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> ProcessMap = CreateProcessMap();

    public static string Detect(string processName, string? executablePath, string? applicationName)
    {
        string normalized = Normalize(processName);

        // Known apps always win (Cursor/VS Code ship Chromium .pak files — not Unreal games).
        if (ProcessMap.TryGetValue(normalized, out string? mapped))
        {
            return mapped;
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            string path = executablePath.ToLowerInvariant();
            if (GamePathMarkers.Any(marker => path.Contains(marker, StringComparison.Ordinal)))
            {
                return "Games";
            }

            if (path.Contains(@"\windowsapps\", StringComparison.Ordinal)
                && (path.Contains("game", StringComparison.Ordinal)
                    || path.Contains("xbox", StringComparison.Ordinal)))
            {
                return "Games";
            }

            if (LooksLikeUnrealShippingExe(path) || LooksLikeLocalGameInstall(executablePath))
            {
                return "Games";
            }
        }

        // Soft name hints (no false-positive heavy words like "manager")
        string haystack = $"{normalized} {applicationName}".ToLowerInvariant();
        if (haystack.Contains("steam", StringComparison.Ordinal)
            || haystack.Contains("epicgames", StringComparison.Ordinal)
            || haystack.Contains("battle.net", StringComparison.Ordinal)
            || haystack.Contains("riotclient", StringComparison.Ordinal)
            || haystack.Contains("valorant", StringComparison.Ordinal)
            || haystack.Contains("league of legends", StringComparison.Ordinal)
            || haystack.Contains("minecraft", StringComparison.Ordinal)
            || haystack.Contains("roblox", StringComparison.Ordinal)
            || haystack.Contains("fortnite", StringComparison.Ordinal)
            || haystack.Contains("gta", StringComparison.Ordinal)
            || haystack.Contains("elden", StringComparison.Ordinal)
            || haystack.Contains("cyberpunk", StringComparison.Ordinal))
        {
            return "Games";
        }

        if (haystack.Contains("chrome", StringComparison.Ordinal)
            || haystack.Contains("firefox", StringComparison.Ordinal)
            || haystack.Contains("msedge", StringComparison.Ordinal)
            || haystack.Contains("brave", StringComparison.Ordinal)
            || haystack.Contains("opera", StringComparison.Ordinal))
        {
            return "Browser";
        }

        return "Other";
    }

    private static bool LooksLikeUnrealShippingExe(string pathLower) =>
        pathLower.EndsWith("-win64-shipping.exe", StringComparison.Ordinal)
        || pathLower.EndsWith("-win32-shipping.exe", StringComparison.Ordinal);

    private static bool LooksLikeLocalGameInstall(string executablePath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            lock (LocalGameDirCache)
            {
                if (LocalGameDirCache.TryGetValue(directory, out bool cached))
                {
                    return cached;
                }
            }

            bool isGame = DirectoryHasGameMarkers(directory);
            if (!isGame)
            {
                string? parent = Directory.GetParent(directory)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    isGame = DirectoryHasGameMarkers(parent);
                }
            }

            lock (LocalGameDirCache)
            {
                LocalGameDirCache[directory] = isGame;
            }

            return isGame;
        }
        catch
        {
            return false;
        }
    }

    private static bool DirectoryHasGameMarkers(string directory)
    {
        foreach (string sibling in GameSiblingFiles)
        {
            if (File.Exists(Path.Combine(directory, sibling)))
            {
                return true;
            }
        }

        // Unreal layout / anti-cheat folders (detection only)
        if (Directory.Exists(Path.Combine(directory, "Engine"))
            || Directory.Exists(Path.Combine(directory, "EasyAntiCheat"))
            || Directory.Exists(Path.Combine(directory, "BattlEye"))
            || Directory.Exists(Path.Combine(directory, "Binaries", "Win64")))
        {
            return true;
        }

        return false;
    }

    private static Dictionary<string, string> CreateProcessMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string category, params string[] names)
        {
            foreach (string name in names)
            {
                map[Normalize(name)] = category;
            }
        }

        Add("Browser",
            "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe");

        Add("Work",
            "Code.exe", "devenv.exe", "Cursor.exe", "idea64.exe", "phpstorm64.exe", "webstorm64.exe",
            "Notion.exe", "Figma.exe", "WindowsTerminal.exe", "powershell.exe", "pwsh.exe",
            "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE", "notepad.exe", "Notepad++.exe");

        Add("Communication",
            "Discord.exe", "Slack.exe", "Teams.exe", "ms-teams.exe", "OUTLOOK.EXE",
            "WhatsApp.exe", "Telegram.exe", "Zoom.exe", "Skype.exe");

        Add("Entertainment",
            "Spotify.exe", "vlc.exe", "Music.UI.exe", "Video.UI.exe", "netflix.exe",
            "primevideo.exe", "YouTube.exe", "tiktok.exe");

        Add("System",
            "explorer.exe", "Taskmgr.exe", "SystemSettings.exe", "ShellExperienceHost.exe",
            "SearchHost.exe", "ApplicationFrameHost.exe", "dwm.exe");

        // Launchers + common games → Games (auto)
        Add("Games",
            "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "FortniteClient-Win64-Shipping.exe",
            "GalaxyClient.exe", "Battle.net.exe", "RiotClientServices.exe",
            "LeagueClient.exe", "LeagueClientUx.exe", "VALORANT.exe", "VALORANT-Win64-Shipping.exe",
            "RobloxPlayerBeta.exe", "RobloxStudioBeta.exe", "Minecraft.Windows.exe", "javaw.exe",
            "GTA5.exe", "PlayGTAV.exe", "RocketLeague.exe", "cs2.exe", "csgo.exe",
            "r5apex.exe", "Overwatch.exe", "Destiny2.exe", "ModernWarfare.exe", "cod.exe",
            "eldenring.exe", "Cyberpunk2077.exe", "Witcher3.exe", "Hollow_Knight.exe",
            "Among Us.exe", "FallGuys_client.exe", "RainbowSix.exe", "FC24.exe", "FIFA.exe",
            "NBA2K.exe", "Republique.exe", "GameBar.exe", "GameBarFTServer.exe",
            "XboxPcApp.exe", "XboxApp.exe", "GamingServices.exe");

        return map;
    }

    private static string Normalize(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe";
}

public static class ProcessIgnore
{
    public static readonly string[] DefaultIgnored =
    [
        "LockApp.exe",
        "SearchHost.exe",
        "SearchUI.exe",
        "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe",
        "TextInputHost.exe",
        "SystemSettings.exe",
        "ApplicationFrameHost.exe",
        "explorer.exe",
        "dwm.exe",
        "Taskmgr.exe",
        "SecurityHealthSystray.exe"
    ];

    public static bool IsIgnored(string? processName, IEnumerable<string>? ignoreList)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        foreach (string ignored in ignoreList ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(ignored))
            {
                continue;
            }

            if (string.Equals(Normalize(processName), Normalize(ignored), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value) =>
        value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe";
}
