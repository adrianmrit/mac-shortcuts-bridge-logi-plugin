using System.Collections.Concurrent;
using MacShortcutsBridge.Core;

namespace MacShortcutsBridge.Core.Tests;

[TestFixture]
public sealed class ShortcutIconCacheTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jEioAAAAASUVORK5CYII=");
    private string directory = null!;
    private FakeCache source = null!;
    private ShortcutIconCache icons = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Directory.CreateTempSubdirectory("ShortcutIconCacheTests-").FullName;
        source = new FakeCache();
        icons = new(source, directory, exception => Assert.Fail($"Unexpected preload failure: {exception.GetType().Name}"));
    }

    [TearDown]
    public void TearDown()
    {
        icons.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task QueuePreload_WholeCatalog_CachesWithoutCreatingBindingSnapshots(bool missingDirectory)
    {
        if (missingDirectory) { source.MissingException = new DirectoryNotFoundException(); }
        var identifiers = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var ready = Completion();
        source.AfterCapture = () => { if (source.Images.Count == identifiers.Length) { ready.TrySetResult(); } };
        icons.QueuePreload(identifiers.Concat(identifiers));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(source.Captures, Is.EqualTo(identifiers.Length));
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        });
    }

    [Test]
    public async Task CaptureAsync_PreloadAlreadyInFlight_JoinsSingleExtraction()
    {
        var identifier = Guid.NewGuid();
        var started = Completion();
        var release = Completion();
        source.CaptureHandler = () => { started.TrySetResult(); return release.Task; };
        icons.QueuePreload([identifier]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var key = NewKey();
        var selection = icons.CaptureAsync(identifier, key);
        release.SetResult();
        await selection.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(source.Captures, Is.EqualTo(1));
            Assert.That(icons.Read(key), Is.EqualTo(Png));
        });
    }

    [Test]
    public async Task TryCopyCached_WarmedIcon_CreatesImmediateIndependentSnapshot()
    {
        var identifier = Guid.NewGuid();
        var first = NewKey();
        await icons.CaptureAsync(identifier, first);
        var second = NewKey();
        Assert.That(icons.TryCopyCached(identifier, second), Is.True);
        File.WriteAllBytes(Path.Combine(directory, first + ".png"), [.. Png, 1]);
        Assert.Multiple(() =>
        {
            Assert.That(source.Captures, Is.EqualTo(1));
            Assert.That(icons.Read(second), Is.EqualTo(Png));
        });
    }

    [Test]
    public void TryCopyCached_ExistingSnapshot_OverwritesWithCachedIcon()
    {
        var identifier = Guid.NewGuid();
        source.Images[ShortcutIconCache.CacheKey(identifier)] = Png;
        var key = NewKey();
        File.WriteAllBytes(Path.Combine(directory, key + ".png"), [.. Png, 1]);

        Assert.That(icons.TryCopyCached(identifier, key), Is.True);
        Assert.That(icons.Read(key), Is.EqualTo(Png));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TryCopyCached_ColdIcon_DoesNotLaunchExtractionOrCreateSnapshot(bool missingDirectory)
    {
        if (missingDirectory) { source.MissingException = new DirectoryNotFoundException(); }
        Assert.That(icons.TryCopyCached(Guid.NewGuid(), NewKey()), Is.False);
        Assert.That(source.Captures, Is.Zero);
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TryCopyCached_ReadFailure_DoesNotTreatAsCacheMiss(bool unauthorized)
    {
        var failure = unauthorized ? (Exception)new UnauthorizedAccessException() : new IOException();
        source.MissingException = failure;

        Assert.That(Assert.Throws(failure.GetType(), () => icons.TryCopyCached(Guid.NewGuid(), NewKey())),
            Is.SameAs(failure));
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReadSnapshot_ReadFailure_PreservesOriginalException(bool unauthorized)
    {
        var failure = unauthorized ? (Exception)new UnauthorizedAccessException() : new IOException();
        source.MissingException = failure;

        Assert.That(Assert.Throws(failure.GetType(), () => ShortcutIcons.ReadSnapshot(source, NewKey(), Guid.NewGuid())),
            Is.SameAs(failure));
        Assert.That(source.Captures, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReadSnapshot_MissingSnapshot_UsesOnlyExactSelectedCacheEntry(bool missingDirectory)
    {
        var identifier = Guid.NewGuid();
        byte[] expected = [.. Png, 1];
        source.Images[ShortcutIconCache.CacheKey(identifier)] = expected;
        source.Images[ShortcutIconCache.CacheKey(Guid.NewGuid())] = [.. Png, 2];
        var key = NewKey();

        using var nested = new ShortcutIconCache(source, Path.Combine(directory, "Missing"), _ => Assert.Fail());
        var snapshots = missingDirectory ? nested : icons;
        var actual = ShortcutIcons.ReadSnapshot(snapshots, key, identifier);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(snapshots.Read(key), Is.EqualTo(expected));
            Assert.That(source.Captures, Is.Zero);
        });
    }

    [Test]
    public void ReadSnapshot_ExistingSnapshot_PreservesSnapshotEvenWhenCacheDiffers()
    {
        var identifier = Guid.NewGuid();
        source.Images[ShortcutIconCache.CacheKey(identifier)] = Png;
        byte[] expected = [.. Png, 1];
        var key = NewKey();
        File.WriteAllBytes(Path.Combine(directory, key + ".png"), expected);

        Assert.That(ShortcutIcons.ReadSnapshot(icons, key, identifier), Is.EqualTo(expected));
        Assert.That(icons.Read(key), Is.EqualTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReadSnapshot_NoMatchingCache_DoesNotCaptureOrUseAnotherShortcut(bool missingDirectory)
    {
        source.Images[ShortcutIconCache.CacheKey(Guid.NewGuid())] = Png;
        using var nested = new ShortcutIconCache(source, Path.Combine(directory, "Missing"), _ => Assert.Fail());
        var snapshots = missingDirectory ? nested : icons;

        Assert.Throws(missingDirectory ? typeof(DirectoryNotFoundException) : typeof(FileNotFoundException),
            () => ShortcutIcons.ReadSnapshot(snapshots, NewKey(), Guid.NewGuid()));
        Assert.Multiple(() =>
        {
            Assert.That(source.Captures, Is.Zero);
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReadSnapshot_NoValidIdentifier_DoesNotGuessCacheEntry(bool empty)
    {
        source.Images[ShortcutIconCache.CacheKey(Guid.NewGuid())] = Png;

        Assert.Throws<FileNotFoundException>(() => ShortcutIcons.ReadSnapshot(icons, NewKey(), empty ? Guid.Empty : null));
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public void ReadSnapshot_CorruptExistingSnapshot_ReportsFailureWithoutReplacingIt()
    {
        var identifier = Guid.NewGuid();
        source.Images[ShortcutIconCache.CacheKey(identifier)] = Png;
        var key = NewKey();
        File.WriteAllBytes(Path.Combine(directory, key + ".png"), [1, 2, 3]);

        Assert.Throws<IOException>(() => ShortcutIcons.ReadSnapshot(icons, key, identifier));
        Assert.That(File.ReadAllBytes(Path.Combine(directory, key + ".png")), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public async Task CaptureAsync_Reload_ReusesPersistentDiskCacheWithoutProcesses()
    {
        var processes = new FakeProcesses();
        var cacheDirectory = Path.Combine(directory, "Cache");
        var identifier = Guid.NewGuid();
        using (var first = new ShortcutIconCache(new ShortcutIcons(processes, cacheDirectory), directory, _ => Assert.Fail()))
        {
            await first.CaptureAsync(identifier, NewKey());
        }
        using var reloaded = new ShortcutIconCache(new ShortcutIcons(processes, cacheDirectory), directory, _ => Assert.Fail());
        var key = NewKey();
        Assert.That(reloaded.TryCopyCached(identifier, key), Is.True);
        Assert.That(processes.Requests, Is.EqualTo(2), "Only the initial AppleScript and conversion should run.");
        Assert.That(reloaded.Read(key), Is.EqualTo(Png));
    }

    [Test]
    public async Task CaptureAsync_AnotherInstanceFinishesCacheDuringReload_UsesVerifiedImage()
    {
        var identifier = Guid.NewGuid();
        source.CaptureHandler = () =>
        {
            source.Images[ShortcutIconCache.CacheKey(identifier)] = Png;
            return Task.FromException(new IOException("Another instance already wrote the cache entry."));
        };
        var key = NewKey();
        await icons.CaptureAsync(identifier, key);
        Assert.That(icons.Read(key), Is.EqualTo(Png));
    }

    [Test]
    public async Task CaptureAsync_ManySelections_BoundsConcurrentExtractionsToTwo()
    {
        var twoStarted = Completion();
        var release = Completion();
        source.CaptureHandler = () =>
        {
            if (source.Captures == 2) { twoStarted.TrySetResult(); }
            return release.Task;
        };
        var selections = Enumerable.Range(0, 8)
            .Select(_ => icons.CaptureAsync(Guid.NewGuid(), NewKey())).ToArray();
        try { await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(selections).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(source.MaxActive, Is.EqualTo(2));
    }

    [Test]
    public async Task QueuePreload_PermissionDenied_ReportsOnceAndStopsBatch()
    {
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var denied = new ShortcutIconCache(source, directory, exception => failure.TrySetResult(exception));
        source.CaptureHandler = () => Task.FromException(new IOException("Permission denied"));
        denied.QueuePreload(Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()));
        Assert.That(await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.TypeOf<IOException>());
        Assert.That(source.Captures, Is.EqualTo(1));
    }

    [Test]
    public async Task Dispose_Preloading_DoesNotStartRemainingIcons()
    {
        var started = Completion();
        var release = Completion();
        var finished = Completion();
        source.CaptureHandler = () => { started.TrySetResult(); return release.Task; };
        source.AfterCapture = () => finished.TrySetResult();
        icons.QueuePreload([Guid.NewGuid(), Guid.NewGuid()]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        icons.Dispose();
        icons.QueuePreload([Guid.NewGuid()]);
        release.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(source.Captures, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public void TryCopyCached_CorruptCache_ReportsFailureWithoutPublishingSnapshot()
    {
        var identifier = Guid.NewGuid();
        source.Images[ShortcutIconCache.CacheKey(identifier)] = [1, 2, 3];
        Assert.Throws<IOException>(() => icons.TryCopyCached(identifier, NewKey()));
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public void QueuePreload_EmptyIdentifier_RejectsBeforeStartingBackgroundWork() =>
        Assert.Throws<ArgumentException>(() => icons.QueuePreload([Guid.Empty]));

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string NewKey() => (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")).ToUpperInvariant();

    private sealed class FakeCache : IShortcutIcons
    {
        private readonly object gate = new();
        private int active;
        public int Captures;
        public int MaxActive { get; private set; }
        public ConcurrentDictionary<string, byte[]> Images { get; } = new();
        public Func<Task>? CaptureHandler { get; set; }
        public Action? AfterCapture { get; set; }
        public Exception MissingException { get; set; } = new FileNotFoundException();

        public async Task CaptureAsync(Guid identifier, string key)
        {
            Interlocked.Increment(ref Captures);
            lock (gate) { active++; MaxActive = Math.Max(MaxActive, active); }
            try
            {
                if (CaptureHandler is { } capture) { await capture(); }
                Images[key] = Png;
                AfterCapture?.Invoke();
            }
            finally { lock (gate) { active--; } }
        }

        public byte[] Read(string key) => Images.TryGetValue(key, out var bytes) ? bytes : throw MissingException;
    }

    private sealed class FakeProcesses : IProcessRunner
    {
        public int Requests { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request)
        {
            Requests++;
            File.WriteAllBytes(request.Arguments[^1], Png);
            return Task.FromResult(new ProcessResult(0, ""));
        }
    }
}
