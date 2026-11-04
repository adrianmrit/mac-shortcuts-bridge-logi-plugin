namespace MacShortcutsBridge.Core;

public sealed record CatalogSnapshot(IReadOnlyList<Shortcut> Shortcuts, bool IsLoading, bool HasLoaded, string? Error);

public sealed class ShortcutCatalog(ShortcutsClient client)
{
    private readonly object gate = new();
    private CatalogSnapshot snapshot = new([], false, false, null);
    private Task? refreshing;

    public event Action? Changed;

    public CatalogSnapshot Snapshot
    {
        get { lock (gate) { return snapshot; } }
    }

    public Task RefreshAsync()
    {
        lock (gate)
        {
            if (refreshing is { IsCompleted: false })
            {
                return refreshing;
            }
            snapshot = snapshot with { IsLoading = true, Error = null };
            refreshing = Task.Run(RefreshCoreAsync);
            return refreshing;
        }
    }

    private async Task RefreshCoreAsync()
    {
        CatalogSnapshot next;
        try
        {
            var shortcuts = await client.DiscoverAsync().ConfigureAwait(false);
            next = new CatalogSnapshot(shortcuts, false, true, null);
        }
        catch (Exception exception) when (exception is IOException or FormatException or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            // Never expose subprocess output or system exception payloads in the editor.
            next = new CatalogSnapshot([], false, true,
                "Could not load shortcuts. Open the Shortcuts app, then reopen this list.");
        }
        lock (gate) { snapshot = next; }
        Changed?.Invoke();
    }
}
