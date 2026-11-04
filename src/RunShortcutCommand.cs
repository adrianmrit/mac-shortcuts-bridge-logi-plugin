using System.Security.Cryptography;
using MacShortcutsBridge.Core;

namespace Loupedeck.MacShortcutsBridgePlugin;

public sealed class RunShortcutCommand : ActionEditorCommand
{
    internal const string ShortcutControl = "shortcut";
    internal const string NameControl = "shortcutname";
    internal const string IconControl = "shortcuticon";
    private const int KeypadCanvasSize = 256;
    private readonly record struct ImageArea(int XPercent, int YPercent, int WidthPercent, int HeightPercent);
    private static readonly ImageArea KeypadImageArea = new(
        XPercent: 25, YPercent: 20, WidthPercent: 50, HeightPercent: 50);
    private static readonly ImageArea DefaultImageArea = new(
        XPercent: 21, YPercent: 14, WidthPercent: 58, HeightPercent: 58);
    private readonly object gate = new();
    private ShortcutCatalog? catalog;
    private ShortcutsClient? client;
    private IShortcutIcons? icons;
    private Action<string, Exception?>? failure;
    private Action? success;
    private Action? updateIconTemplates;
    private bool attached;
    private int lifetime;
    private bool listInvalidated;
    private string editorIconKey = "";

    public RunShortcutCommand() : base(DeviceType.Loupedeck70 | DeviceType.Loupedeck72)
    {
        Name = "RunShortcut";
        DisplayName = "Run Shortcut";
        GroupName = "Shortcuts";
        Description = "Run a shortcut from the macOS Shortcuts app. Shortcut icon will be applied automatically after saving, see \"Known Issues\" for details.";
        SetSupportedOs(LoupedeckOperatingSystem.Mac);
        ActionEditor.AddControlEx(new ActionEditorListbox(ShortcutControl, "Shortcut")
            .SetPlaceholder("Search shortcuts...")
            .SetDescription("After selecting a shortcut, save the action to apply its icon to the preview. No manual icon change is needed."));
        ActionEditor.AddControlEx(new ActionEditorHidden(NameControl));
        ActionEditor.AddControlEx(new ActionEditorHidden(IconControl));
        ActionEditor.Started += OnEditorStarted;
        ActionEditor.Finished += (_, e) =>
        {
            Action? updateTemplates = null;
            lock (gate)
            {
                listInvalidated = false;
                editorIconKey = "";
                if (!e.IsCanceled && attached) { updateTemplates = updateIconTemplates; }
            }
            updateTemplates?.Invoke();
        };
        ActionEditor.ControlValueChanged += OnControlValueChanged;
        ActionEditor.ListboxItemsRequested += OnListboxItemsRequested;
        ActionEditor.ControlsStateRequested += OnControlsStateRequested;
    }

    internal void Attach(ShortcutsClient shortcuts, ShortcutCatalog shortcutsCatalog, IShortcutIcons shortcutIcons,
        Action<string, Exception?> reportFailure, Action reportSuccess, Action? updateIconTemplates = null)
    {
        lock (gate)
        {
            if (catalog is not null) { catalog.Changed -= OnCatalogChanged; }
            client = shortcuts;
            catalog = shortcutsCatalog;
            icons = shortcutIcons;
            failure = reportFailure;
            success = reportSuccess;
            this.updateIconTemplates = updateIconTemplates;
            attached = true;
            lifetime++;
            catalog.Changed += OnCatalogChanged;
        }
    }

    protected override bool OnLoad()
    {
        if (Plugin is not MacShortcutsBridgePlugin owner) { return false; }
        Attach(owner.Shortcuts, owner.Catalog, owner.Icons, owner.ReportFailure, owner.ReportSuccess,
            owner.QueueIconTemplateUpdate);
        return true;
    }

    protected override bool OnUnload()
    {
        lock (gate)
        {
            attached = false;
            lifetime++;
            if (catalog is not null) { catalog.Changed -= OnCatalogChanged; }
            client = null;
            catalog = null;
            icons = null;
            failure = null;
            success = null;
            updateIconTemplates = null;
        }
        return true;
    }

    private void OnEditorStarted(object? sender, ActionEditorStartedEventArgs e)
    {
        var iconKey = e.ActionEditorState.GetControlValue(IconControl);
        lock (gate)
        {
            listInvalidated = false;
            editorIconKey = iconKey;
        }
        UpdateControls(e.ActionEditorState);
    }

    private void OnControlsStateRequested(object? sender, ActionEditorControlsStateRequestedEventArgs e) =>
        UpdateControls(e.ActionEditorState);

    private void UpdateControls(ActionEditorState state)
    {
        var snapshot = catalog?.Snapshot;
        if (snapshot is null || snapshot.IsLoading || !snapshot.HasLoaded)
        {
            state.SetValidity(ShortcutControl, false, "Loading shortcuts...");
            return;
        }
        if (snapshot.Error is { } error)
        {
            state.SetValidity(ShortcutControl, false, error);
            return;
        }
        var selected = state.GetControlValue(ShortcutControl);
        var valid = Guid.TryParse(selected, out var identifier)
            && snapshot.Shortcuts.Any(item => item.Id == identifier);
        var hint = snapshot.Shortcuts.Count == 0 ? "No shortcuts found. Create one in Shortcuts, then reopen the list."
            : string.IsNullOrEmpty(selected) ? "Select a shortcut."
            : "Select a shortcut from the list, or reopen it to try again.";
        state.SetValidity(ShortcutControl, valid, valid ? "" : hint);
    }

    private void OnListboxItemsRequested(object? sender, ActionEditorListboxItemsRequestedEventArgs e)
    {
        if (!string.Equals(e.ControlName, ShortcutControl, StringComparison.OrdinalIgnoreCase)) { return; }
        ShortcutCatalog? source;
        bool refresh;
        lock (gate)
        {
            if (!attached) { return; }
            source = catalog;
            // The host requests items again after ListboxItemsChanged; that request
            // consumes the completed result instead of starting another discovery.
            refresh = !listInvalidated;
            listInvalidated = false;
        }
        if (refresh) { _ = source?.RefreshAsync(); }
        var snapshot = source?.Snapshot;
        if (snapshot is null) { return; }
        var selected = e.ActionEditorState.GetControlValue(ShortcutControl);
        if (snapshot.IsLoading || !snapshot.HasLoaded)
        {
            e.AddItem("", "Loading shortcuts...", "");
        }
        else if (snapshot.Error is { } error)
        {
            e.AddItem("", error, "");
        }
        else
        {
            var query = Guid.TryParse(selected, out _) ? "" : selected;
            foreach (var shortcut in snapshot.Shortcuts.Where(item =>
                item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            {
                e.AddItem(shortcut.Identifier, shortcut.GetChoiceLabel(snapshot.Shortcuts), "");
            }
            if (e.Items.Count == 0)
            {
                e.AddItem("", snapshot.Shortcuts.Count == 0 ? "No shortcuts found." : "No matching shortcuts.", "");
            }
        }
        if (Guid.TryParse(selected, out _)
            && (snapshot.IsLoading || snapshot.Error is not null
                || !snapshot.Shortcuts.Any(item => string.Equals(item.Identifier, selected, StringComparison.OrdinalIgnoreCase))))
        {
            var name = e.ActionEditorState.GetControlValue(NameControl);
            var suffix = snapshot.IsLoading || snapshot.Error is not null ? "list unavailable" : "unavailable";
            e.AddItem(selected, $"{(string.IsNullOrEmpty(name) ? "Saved shortcut" : name)} ({suffix})", "");
        }
        if (Guid.TryParse(selected, out _)) { e.SetSelectedItemName(selected); }
        UpdateControls(e.ActionEditorState);
    }

    private void OnControlValueChanged(object? sender, ActionEditorControlValueChangedEventArgs e)
    {
        if (!string.Equals(e.ControlName, ShortcutControl, StringComparison.OrdinalIgnoreCase)) { return; }
        lock (gate) { if (!attached) { return; } }
        var state = e.ActionEditorState;
        var selectedShortcut = state.GetControlValue(ShortcutControl);
        var shortcut = Guid.TryParse(selectedShortcut, out var id)
            ? catalog?.Snapshot.Shortcuts.FirstOrDefault(item => item.Id == id)
            : null;
        if (shortcut is null)
        {
            NotifyListChanged();
            UpdateControls(state);
            return;
        }

        state.SetValue(NameControl, shortcut.Name);
        state.SetDisplayName(shortcut.Name);
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (gate) { editorIconKey = key; }
        var iconSource = icons;
        var cached = false;
        if (iconSource is ICachedShortcutIcons cache)
        {
            try { cached = cache.TryCopyCached(shortcut.Id, key); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Plugin?.Log.Error($"Could not reuse a shortcut icon. Error type: {exception.GetType().Name}.");
            }
        }
        state.SetValue(IconControl, key);
        if (cached)
        {
            ActionImageChanged();
            updateIconTemplates?.Invoke();
        }
        else if (iconSource is null)
        {
            ActionImageChanged();
        }
        else
        {
            _ = CaptureIconAsync(iconSource, shortcut.Id, key);
        }
        UpdateControls(state);
    }

    private async Task CaptureIconAsync(IShortcutIcons source, Guid identifier, string key)
    {
        int generation;
        lock (gate) { generation = lifetime; }
        try
        {
            await source.CaptureAsync(identifier, key);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            if (exception is not OperationCanceledException)
            {
                Plugin?.Log.Error($"Could not capture a shortcut icon. Error type: {exception.GetType().Name}.");
            }
        }
        PublishCapturedIcon(generation);
    }

    private void PublishCapturedIcon(int generation)
    {
        Action? updateTemplates;
        lock (gate)
        {
            if (!attached || lifetime != generation) { return; }
            updateTemplates = updateIconTemplates;
        }
        ActionImageChanged();
        ActionEditor.InvalidateControls();
        updateTemplates?.Invoke();
    }

    private void OnCatalogChanged()
    {
        Action<string, Exception?>? reportFailure;
        string? error;
        lock (gate)
        {
            if (!attached) { return; }
            error = catalog?.Snapshot.Error;
            reportFailure = failure;
            listInvalidated = true;
        }
        if (error is not null) { reportFailure?.Invoke(error, null); }
        ActionEditor.ListboxItemsChanged(ShortcutControl);
        ActionEditor.InvalidateControls();
    }

    private void NotifyListChanged()
    {
        lock (gate)
        {
            if (!attached) { return; }
            listInvalidated = true;
        }
        ActionEditor.ListboxItemsChanged(ShortcutControl);
    }

    protected override bool RunCommand(ActionEditorActionParameters actionParameters)
    {
        var selected = actionParameters.GetString(ShortcutControl, "");
        if (!Guid.TryParse(selected, out var identifier) || identifier == Guid.Empty)
        {
            Action<string, Exception?>? reportFailure;
            lock (gate) { reportFailure = failure; }
            reportFailure?.Invoke("Select a shortcut in this action's configuration.", null);
            return false;
        }
        ShortcutsClient? runner;
        lock (gate) { runner = attached ? client : null; }
        if (runner is null) { return false; }
        _ = RunAsync(runner, identifier);
        return true;
    }

    internal async Task RunAsync(ShortcutsClient shortcuts, Guid identifier)
    {
        int generation;
        lock (gate) { generation = lifetime; }
        try
        {
            await shortcuts.RunAsync(identifier).ConfigureAwait(false);
            Action? reportSuccess;
            lock (gate) { reportSuccess = attached && lifetime == generation ? success : null; }
            reportSuccess?.Invoke();
        }
        catch (Exception exception) when (exception is IOException or ArgumentException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Action<string, Exception?>? reportFailure;
            lock (gate)
            {
                reportFailure = attached && lifetime == generation ? failure : null;
            }
            reportFailure?.Invoke("Shortcut could not run. Check it in Shortcuts, including permissions, or reselect it in this action.", exception);
        }
    }

    protected override string GetCommandDisplayName(ActionEditorActionParameters actionParameters)
    {
        var name = actionParameters.GetString(NameControl, "");
        return string.IsNullOrEmpty(name) ? "Run Shortcut" : name;
    }

    protected override BitmapImage GetCommandImage(ActionEditorActionParameters actionParameters, int width, int height)
    {
        var key = actionParameters.GetString(IconControl, "");
        if (string.IsNullOrEmpty(key))
        {
            lock (gate) { key = editorIconKey; }
        }
        if (!string.IsNullOrEmpty(key) && icons is { } source)
        {
            try
            {
                Guid? identifier = Guid.TryParse(actionParameters.GetString(ShortcutControl, ""), out var id)
                    && id != Guid.Empty ? id : null;
                var bytes = ShortcutIcons.ReadSnapshot(source, key, identifier);
                ShortcutIcons.ValidatePng(bytes);
                if (!BitmapImage.TryCreateFromArray(bytes, out var image))
                {
                    throw new IOException("The cached shortcut icon is invalid.");
                }
                return Resize(image, width, height);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException
                or UnauthorizedAccessException)
            {
                // Missing or unreadable native snapshots use the plugin default.
            }
        }
        return DefaultActionIcon.Render(width, height, actionParameters.GetString(NameControl, ""));
    }

    internal static byte[] PadShortcutIconForKeypad(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0 || !BitmapImage.TryCreateFromArray(bytes, out var image))
        {
            throw new IOException("The shortcut icon could not be decoded.");
        }
        using (image)
        using (var builder = new BitmapBuilder(KeypadCanvasSize, KeypadCanvasSize))
        {
            builder.Clear(BitmapColor.Transparent);
            DrawWithinArea(builder, image, KeypadImageArea);
            using var padded = builder.ToImage();
            return padded.ToArray();
        }
    }

    private static BitmapImage Resize(BitmapImage image, int width, int height)
    {
        using (image)
        using (var builder = new BitmapBuilder(width, height))
        {
            builder.Clear(BitmapColor.Transparent);
            DrawWithinArea(builder, image, DefaultImageArea);
            return builder.ToImage();
        }
    }

    private static void DrawWithinArea(BitmapBuilder builder, BitmapImage image, ImageArea area)
    {
        var width = builder.Width;
        var height = builder.Height;
        var x = width * area.XPercent / 100;
        var y = height * area.YPercent / 100;
        var availableWidth = Math.Max(1, width * area.WidthPercent / 100);
        var availableHeight = Math.Max(1, height * area.HeightPercent / 100);
        var scale = Math.Min((double)availableWidth / image.Width, (double)availableHeight / image.Height);
        var w = Math.Clamp((int)Math.Round(image.Width * scale), 1, availableWidth);
        var h = Math.Clamp((int)Math.Round(image.Height * scale), 1, availableHeight);
        builder.DrawImage(image, x + (availableWidth - w) / 2, y + (availableHeight - h) / 2, w, h);
    }
}
