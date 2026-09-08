using UsageDock.Services;
using Xunit;

namespace UsageDock.Tests;

public sealed class HookInstallerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usagedock-hooks-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public HookInstallerTests() => Directory.CreateDirectory(_directory);

    private HookInstaller Installer() => new(SettingsPath, @"C:\apps\UsageDock.exe");

    [Fact]
    public void Install_creates_a_settings_file_with_every_hook_event()
    {
        var installer = Installer();

        Assert.True(installer.Install());
        Assert.True(installer.IsInstalled());

        var text = File.ReadAllText(SettingsPath);
        foreach (var (name, _, _) in HookInstaller.HookMap)
        {
            Assert.Contains(name, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Install_preserves_hooks_belonging_to_other_tools()
    {
        File.WriteAllText(SettingsPath, """
        {
          "theme": "dark",
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "other-tool.exe done" } ] } ]
          }
        }
        """);

        Installer().Install();

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("other-tool.exe done", text, StringComparison.Ordinal);
        Assert.Contains("\"theme\"", text, StringComparison.Ordinal);
        Assert.Contains(HookInstaller.Marker, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Installing_twice_does_not_duplicate_entries()
    {
        var installer = Installer();
        installer.Install();
        installer.Install();

        var text = File.ReadAllText(SettingsPath);
        var occurrences = text.Split(HookInstaller.Marker).Length - 1;

        Assert.Equal(HookInstaller.HookMap.Length, occurrences);
    }

    [Fact]
    public void Uninstall_removes_only_our_entries()
    {
        File.WriteAllText(SettingsPath, """
        {
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "other-tool.exe done" } ] } ]
          }
        }
        """);

        var installer = Installer();
        installer.Install();

        Assert.True(installer.Uninstall());
        Assert.False(installer.IsInstalled());

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("other-tool.exe done", text, StringComparison.Ordinal);
        Assert.DoesNotContain(HookInstaller.Marker, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_backs_up_the_original_settings()
    {
        File.WriteAllText(SettingsPath, """{ "theme": "dark" }""");

        Installer().Install();

        var backup = SettingsPath + ".usagedock-bak";
        Assert.True(File.Exists(backup));
        Assert.Contains("dark", File.ReadAllText(backup), StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_reports_false_when_nothing_was_installed()
    {
        File.WriteAllText(SettingsPath, """{ "theme": "dark" }""");

        Assert.False(Installer().Uninstall());
    }

    [Fact]
    public void The_command_quotes_the_executable_path()
    {
        var command = Installer().BuildCommand("working");

        var expected = @"""C:\apps\UsageDock.exe"" " + HookInstaller.Marker + " working";

        Assert.Equal(expected, command);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
