using System.Reflection;
using MacShortcutsBridge.Core;

namespace Loupedeck.MacShortcutsBridgePlugin;

public sealed class MacShortcutsBridgePlugin : Plugin
{
    private readonly object gate = new();
    private bool loaded;

    internal ShortcutsClient Shortcuts { get; } = new(new ProcessRunner());
    internal ShortcutCatalog Catalog { get; }
    internal IShortcutIcons Icons => icons ??= new ShortcutIconCache(
        new ShortcutIcons(new ProcessRunner(), Path.Combine(GetPluginDataDirectory(), "ShortcutIcons", "Cache")),
        Path.Combine(GetPluginDataDirectory(), "ShortcutIcons"), exception =>
            Log.Error($"Could not preload shortcut icons. Error type: {exception.GetType().Name}."));
    private IShortcutIcons? icons;
    private ShortcutIconTemplates? iconTemplates;

    public override bool UsesApplicationApiOnly => true;
    public override bool HasNoApplication => true;

    public MacShortcutsBridgePlugin() => Catalog = new ShortcutCatalog(Shortcuts);

    public override void Load()
    {
        lock (gate)
        {
            loaded = true;
            var dataDirectory = GetPluginDataDirectory();
            var serviceDirectory = Directory.GetParent(dataDirectory)?.Parent?.FullName
                ?? throw new IOException("The plugin data directory has no service directory.");
            using var svg = Assembly.GetManifestResourceStream(
                "Loupedeck.MacShortcutsBridgePlugin.Assets.ShortcutsBridge.svg")
                ?? throw new IOException("The default SVG is missing.");
            using var png = Assembly.GetManifestResourceStream(
                "Loupedeck.MacShortcutsBridgePlugin.Assets.Icon256x256.png")
                ?? throw new IOException("The default PNG is missing.");
            using var svgBytes = new MemoryStream();
            using var pngBytes = new MemoryStream();
            svg.CopyTo(svgBytes);
            png.CopyTo(pngBytes);
            iconTemplates = new ShortcutIconTemplates(Path.Combine(serviceDirectory, "Applications"),
                Path.Combine(dataDirectory, "IconTemplateOwnership.json"), Icons,
                new ActionIconHost(Path.Combine(serviceDirectory, "Temp", "WebSocketPort.txt")),
                svgBytes.ToArray(), pngBytes.ToArray(), exception =>
                    Log.Error($"Could not synchronize shortcut icon templates. Error type: {exception.GetType().Name}."),
                RefreshConfigurationImages, RunShortcutCommand.PadShortcutIconForKeypad,
                DefaultActionIcon.Template);
            iconTemplates.QueueUpdate();
            Catalog.Changed += QueueIconPreload;
        }

        OnPluginStatusChanged(Loupedeck.PluginStatus.Normal, "Ready");
    }

    public override void Unload()
    {
        lock (gate)
        {
            loaded = false;
            Catalog.Changed -= QueueIconPreload;
            iconTemplates?.Dispose();
            iconTemplates = null;
            (icons as IDisposable)?.Dispose();
            icons = null;
        }
    }

    internal void QueueIconTemplateUpdate()
    {
        lock (gate) { if (loaded) { iconTemplates?.QueueUpdate(); } }
    }

    internal void RefreshConfigurationImages()
    {
        Action? publishStatus = null;
        lock (gate)
        {
            if (loaded && PluginStatus is { } current)
            {
                // Both 6.4.2 configuration transports invalidate their image caches on this SDK event.
                publishStatus = () =>
                    OnPluginStatusChanged(current.Status, current.Message, current.SupportUrl, current.SupportUrlTitle);
            }
        }
        publishStatus?.Invoke();
    }

    private void QueueIconPreload()
    {
        lock (gate)
        {
            var snapshot = Catalog.Snapshot;
            if (loaded && snapshot.HasLoaded && !snapshot.IsLoading && snapshot.Error is null)
            {
                (Icons as ICachedShortcutIcons)?.QueuePreload(snapshot.Shortcuts.Select(shortcut => shortcut.Id));
            }
        }
    }

    internal void ReportFailure(string message, Exception? exception = null)
    {
        lock (gate)
        {
            if (!loaded) { return; }
            // Exception messages and subprocess streams can contain shortcut output.
            Log.Error($"{message} Error type: {exception?.GetType().Name ?? "Unavailable"}.");
        }
        OnPluginStatusChanged(Loupedeck.PluginStatus.Error, message);
    }

    internal void ReportSuccess()
    {
        bool shouldReport;
        lock (gate)
        {
            shouldReport = loaded;
        }
        if (shouldReport) { OnPluginStatusChanged(Loupedeck.PluginStatus.Normal, "Ready"); }
    }
}
