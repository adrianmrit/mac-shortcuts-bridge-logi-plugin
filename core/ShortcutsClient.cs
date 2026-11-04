namespace MacShortcutsBridge.Core;

public sealed class ShortcutsClient(IProcessRunner processes)
{
    public async Task<IReadOnlyList<Shortcut>> DiscoverAsync()
    {
        var result = await processes.RunAsync(new ProcessRequest(
            "/usr/bin/shortcuts", ["list", "--show-identifiers"], true, TimeSpan.FromSeconds(20)))
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"Could not list shortcuts (exit {result.ExitCode}). Open Shortcuts and reopen the list.");
        }
        return Shortcut.ParseCatalog(result.Output);
    }

    public async Task RunAsync(Guid identifier)
    {
        if (identifier == Guid.Empty)
        {
            throw new ArgumentException("Select a shortcut before running this action.", nameof(identifier));
        }
        var result = await processes.RunAsync(new ProcessRequest(
            "/usr/bin/shortcuts", ["run", identifier.ToString("D").ToUpperInvariant()]))
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException(
                $"Shortcut failed (exit {result.ExitCode}). Check it in Shortcuts, including its permissions, or reselect it in the action.");
        }
    }
}
