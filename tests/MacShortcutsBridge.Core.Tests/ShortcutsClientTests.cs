using MacShortcutsBridge.Core;

namespace MacShortcutsBridge.Core.Tests;

[TestFixture]
public sealed class ShortcutsClientTests
{
    [Test]
    public async Task DiscoverAsync_RequestsIdentifiers_UsesBoundedDiscovery()
    {
        var process = new FakeProcesses();
        var client = new ShortcutsClient(process);
        Assert.That(await client.DiscoverAsync(), Is.Empty);
        var request = process.Requests.Single();
        Assert.That(request, Is.EqualTo(new ProcessRequest("/usr/bin/shortcuts",
            new[] { "list", "--show-identifiers" }, true, TimeSpan.FromSeconds(20))).UsingPropertiesComparer());
    }

    [Test]
    public async Task RunAsync_Identifier_UsesArgumentsWithoutInputOrTimeout()
    {
        var processes = new FakeProcesses();
        var id = Guid.NewGuid();
        await new ShortcutsClient(processes).RunAsync(id);
        var request = processes.Requests.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.FileName, Is.EqualTo("/usr/bin/shortcuts"));
            Assert.That(request.Arguments, Is.EqualTo(new[] { "run", id.ToString("D").ToUpperInvariant() }));
            Assert.That(request.CaptureOutput, Is.False);
            Assert.That(request.Timeout, Is.Null);
        });
    }

    [Test]
    public async Task RunAsync_AnotherRunStillPending_StartsEveryInvocation()
    {
        var completion = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processes = new FakeProcesses { Handler = _ => completion.Task };
        var client = new ShortcutsClient(processes);
        var id = Guid.NewGuid();
        var runs = Enumerable.Range(0, 20).Select(_ => client.RunAsync(id)).ToArray();
        Assert.That(processes.Requests, Has.Count.EqualTo(20));
        Assert.That(runs.All(task => !task.IsCompleted), Is.True);
        completion.SetResult(new ProcessResult(0, ""));
        await Task.WhenAll(runs);
    }

    [Test]
    public void RunAsync_EmptyIdentifier_DoesNotStartProcess()
    {
        var processes = new FakeProcesses();
        Assert.ThrowsAsync<ArgumentException>(() => new ShortcutsClient(processes).RunAsync(Guid.Empty));
        Assert.That(processes.Requests, Is.Empty);
    }

    [Test]
    public void RunAsync_FailedExit_ReportsCodeWithoutOutput()
    {
        var processes = new FakeProcesses { Handler = _ => Task.FromResult(new ProcessResult(7, "private output")) };
        var error = Assert.ThrowsAsync<IOException>(() => new ShortcutsClient(processes).RunAsync(Guid.NewGuid()));
        Assert.That(error!.Message, Does.Contain("exit 7").And.Not.Contain("private output"));
    }

    [Test]
    public async Task RefreshAsync_FailedDiscovery_SurfacesErrorWithoutPayload()
    {
        var processes = new FakeProcesses { Handler = _ => throw new IOException("secret payload") };
        var catalog = new ShortcutCatalog(new ShortcutsClient(processes));
        await catalog.RefreshAsync();
        Assert.That(catalog.Snapshot.Error, Does.Contain("Could not load").And.Not.Contain("secret"));
        Assert.That(catalog.Snapshot.HasLoaded, Is.True);
    }

    [Test]
    public async Task RefreshAsync_AlreadyRefreshing_CoalescesCatalogRequestsOnly()
    {
        var completion = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processes = new FakeProcesses { Handler = _ => completion.Task };
        var catalog = new ShortcutCatalog(new ShortcutsClient(processes));
        var first = catalog.RefreshAsync();
        var second = catalog.RefreshAsync();
        Assert.That(second, Is.SameAs(first));
        completion.SetResult(new ProcessResult(0, ""));
        await first;
        Assert.That(processes.Requests, Has.Count.EqualTo(1));
        Assert.That(catalog.Snapshot.IsLoading, Is.False);
    }

    [Test]
    public async Task RefreshAsync_AfterCompletion_DiscoversAgainInsteadOfReusingCompletedQuery()
    {
        var processes = new FakeProcesses();
        var catalog = new ShortcutCatalog(new ShortcutsClient(processes));
        await catalog.RefreshAsync();
        await catalog.RefreshAsync();
        Assert.That(processes.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task RefreshAsync_DelayedDiscovery_ExposesLoadingThenPublishesNewSnapshot()
    {
        var completion = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processes = new FakeProcesses { Handler = _ => completion.Task };
        var catalog = new ShortcutCatalog(new ShortcutsClient(processes));
        var changes = 0;
        catalog.Changed += () => changes++;
        var pending = catalog.RefreshAsync();
        Assert.That(catalog.Snapshot.IsLoading, Is.True);
        Assert.That(catalog.Snapshot.HasLoaded, Is.False);
        var id = Guid.NewGuid();
        completion.SetResult(new ProcessResult(0, $"New shortcut ({id:D})"));
        await pending;
        Assert.That(catalog.Snapshot.IsLoading, Is.False);
        Assert.That(catalog.Snapshot.Shortcuts.Single().Id, Is.EqualTo(id));
        Assert.That(changes, Is.EqualTo(1));
    }

    internal sealed class FakeProcesses : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Func<ProcessRequest, Task<ProcessResult>> Handler { get; init; } =
            _ => Task.FromResult(new ProcessResult(0, ""));

        public Task<ProcessResult> RunAsync(ProcessRequest request)
        {
            Requests.Add(request);
            return Handler(request);
        }
    }
}
