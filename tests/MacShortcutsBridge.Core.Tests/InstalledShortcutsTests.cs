using MacShortcutsBridge.Core;

namespace MacShortcutsBridge.Core.Tests;

[TestFixture]
[Platform("MacOsX")]
public sealed class InstalledShortcutsTests
{
    [Test]
    [Explicit("Reads the current user's catalog. Does not execute any shortcuts.")]
    public async Task DiscoverAsync_InstalledShortcuts_ReturnsValidUniqueIdentifiers()
    {
        var shortcuts = await new ShortcutsClient(new ProcessRunner()).DiscoverAsync();
        Assert.That(shortcuts.Select(shortcut => shortcut.Id).Distinct().Count(), Is.EqualTo(shortcuts.Count));
        Assert.That(shortcuts.All(shortcut => shortcut.Id != Guid.Empty && shortcut.Name.Length > 0), Is.True);
    }

    [Test]
    [Explicit("Executes only the explicitly supplied benign fixture and can request macOS Automation permission.")]
    public async Task RunAndCaptureIcon_ExplicitFixture_UsesPublicMacOsInterfaces()
    {
        var fixture = Environment.GetEnvironmentVariable("MAC_SHORTCUTS_TEST_ID");
        Assert.That(Guid.TryParse(fixture, out var identifier) && identifier != Guid.Empty, Is.True,
            "Set MAC_SHORTCUTS_TEST_ID to the identifier of a harmless shortcut you authorize this test to execute.");
        var directory = Directory.CreateTempSubdirectory("ShortcutsLiveIcon-");
        try
        {
            var processes = new ProcessRunner();
            await new ShortcutsClient(processes).RunAsync(identifier);
            var icons = new ShortcutIcons(processes, directory.FullName);
            var key = new string('C', 64);
            await icons.CaptureAsync(identifier, key);
            Assert.That(icons.Read(key), Has.Length.GreaterThan(33));
        }
        finally
        {
            directory.Delete(true);
        }
    }

}
