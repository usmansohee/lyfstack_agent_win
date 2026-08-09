using LyfStack.Agent.Windows.Configuration;

namespace LyfStack.Agent.Windows.Tests;

public class AutoCategoryDetectorTests
{
    [Fact]
    public void Detects_steam_path_as_games()
    {
        string category = AutoCategoryDetector.Detect(
            "mygame.exe",
            @"C:\Program Files (x86)\Steam\steamapps\common\MyGame\mygame.exe",
            "My Game");

        Assert.Equal("Games", category);
    }

    [Fact]
    public void Detects_local_unity_install_via_sibling_dll()
    {
        string root = Path.Combine(Path.GetTempPath(), "lyfstack-game-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string exe = Path.Combine(root, "CoolTitle.exe");
            File.WriteAllText(exe, "fake");
            File.WriteAllText(Path.Combine(root, "UnityPlayer.dll"), "fake");

            string category = AutoCategoryDetector.Detect("CoolTitle.exe", exe, "Cool Title");
            Assert.Equal("Games", category);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Detects_unreal_shipping_exe_name()
    {
        string category = AutoCategoryDetector.Detect(
            "MyGame-Win64-Shipping.exe",
            @"D:\Games\MyGame\Binaries\Win64\MyGame-Win64-Shipping.exe",
            "My Game");

        Assert.Equal("Games", category);
    }

    [Fact]
    public void Cursor_is_work_not_games()
    {
        string root = Path.Combine(Path.GetTempPath(), "lyfstack-cursor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string exe = Path.Combine(root, "Cursor.exe");
            File.WriteAllText(exe, "fake");
            // Electron/Chromium resource packs — must NOT trigger Games
            File.WriteAllText(Path.Combine(root, "resources.pak"), "fake");
            File.WriteAllText(Path.Combine(root, "chrome_100_percent.pak"), "fake");

            string category = AutoCategoryDetector.Detect("Cursor.exe", exe, "Cursor");
            Assert.Equal("Work", category);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Does_not_mark_electron_pak_folder_as_game()
    {
        string root = Path.Combine(Path.GetTempPath(), "lyfstack-electron-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string exe = Path.Combine(root, "SomeEditor.exe");
            File.WriteAllText(exe, "fake");
            File.WriteAllText(Path.Combine(root, "resources.pak"), "fake");

            string category = AutoCategoryDetector.Detect("SomeEditor.exe", exe, "Some Editor");
            Assert.Equal("Other", category);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
