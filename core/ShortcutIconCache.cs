using System.Security.Cryptography;

namespace MacShortcutsBridge.Core;

public interface ICachedShortcutIcons : IShortcutIcons
{
    bool TryCopyCached(Guid identifier, string key);
    void QueuePreload(IEnumerable<Guid> identifiers);
}

public sealed class ShortcutIconCache(IShortcutIcons cache, string snapshotsDirectory,
    Action<Exception> reportFailure) : ICachedShortcutIcons, IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim extractionSlots = new(2);
    private readonly Dictionary<Guid, Task> captures = [];
    private readonly CancellationTokenSource stopping = new();
    private Guid[]? pending;
    private Task? preloading;
    private bool disposed;

    public byte[] Read(string key)
    {
        ShortcutIcons.ValidateKey(key);
        return ShortcutIcons.ReadBounded(Path.Combine(snapshotsDirectory, key + ".png"));
    }

    public bool TryCopyCached(Guid identifier, string key)
    {
        ShortcutIcons.ValidateKey(key);
        lock (gate) { if (disposed) { return false; } }
        byte[] bytes;
        try { bytes = cache.Read(CacheKey(identifier)); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        ShortcutIcons.ValidatePng(bytes);
        Directory.CreateDirectory(snapshotsDirectory);
        using var output = new FileStream(Path.Combine(snapshotsDirectory, key + ".png"), FileMode.Create);
        output.Write(bytes);
        return true;
    }

    public async Task CaptureAsync(Guid identifier, string key)
    {
        ShortcutIcons.ValidateKey(key);
        await EnsureCachedAsync(identifier).ConfigureAwait(false);
        if (!TryCopyCached(identifier, key))
        {
            lock (gate) { if (disposed) { throw new OperationCanceledException("The icon cache was unloaded."); } }
            throw new IOException("The captured shortcut icon is missing.");
        }
    }

    private Task EnsureCachedAsync(Guid identifier)
    {
        var cacheKey = CacheKey(identifier);
        lock (gate)
        {
            if (disposed) { return Task.FromCanceled(new CancellationToken(true)); }
            if (captures.TryGetValue(identifier, out var existing)) { return existing; }
            var token = stopping.Token;
            var task = Task.Run(async () =>
            {
                var acquired = false;
                try
                {
                    await extractionSlots.WaitAsync(token).ConfigureAwait(false);
                    acquired = true;
                    token.ThrowIfCancellationRequested();
                    if (HasCached(cacheKey)) { return; }
                    try { await cache.CaptureAsync(identifier, cacheKey).ConfigureAwait(false); }
                    catch (IOException)
                    {
                        // A previous plugin instance can finish the same immutable entry during reload.
                        if (!HasCached(cacheKey)) { throw; }
                    }
                }
                finally
                {
                    if (acquired) { extractionSlots.Release(); }
                    lock (gate) { captures.Remove(identifier); }
                }
            });
            captures.Add(identifier, task);
            return task;
        }
    }

    private bool HasCached(string key)
    {
        try { ShortcutIcons.ValidatePng(cache.Read(key)); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public void QueuePreload(IEnumerable<Guid> identifiers)
    {
        var batch = identifiers.Distinct().ToArray();
        foreach (var identifier in batch) { _ = CacheKey(identifier); }
        lock (gate)
        {
            if (disposed) { return; }
            pending = batch;
            if (preloading is null || preloading.IsCompleted) { preloading = Task.Run(PreloadAsync); }
        }
    }

    private async Task PreloadAsync()
    {
        while (true)
        {
            Guid[] batch;
            lock (gate)
            {
                if (disposed || pending is null) { preloading = null; return; }
                batch = pending;
                pending = null;
            }
            foreach (var identifier in batch)
            {
                try { await EnsureCachedAsync(identifier).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or TimeoutException or System.ComponentModel.Win32Exception)
                {
                    reportFailure(exception);
                    // Do not repeatedly prompt or retry an unavailable scripting interface for the whole library.
                    break;
                }
                lock (gate) { if (disposed) { return; } }
            }
        }
    }

    public static string CacheKey(Guid identifier)
    {
        if (identifier == Guid.Empty) { throw new ArgumentException("A shortcut identifier is required.", nameof(identifier)); }
        return Convert.ToHexString(SHA256.HashData(identifier.ToByteArray()));
    }

    public void Dispose()
    {
        Task? active;
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            pending = null;
            stopping.Cancel();
            active = preloading;
        }
        if (active is null || active.IsCompleted) { stopping.Dispose(); }
        else { _ = active.ContinueWith(_ => stopping.Dispose(), TaskScheduler.Default); }
    }
}
