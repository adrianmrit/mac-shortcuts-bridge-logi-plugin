using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Loupedeck;
using Loupedeck.MacShortcutsBridgePlugin;
using MacShortcutsBridge.Core;

namespace MacShortcutsBridge.Sdk.Tests;

[TestFixture]
[Platform("MacOsX")]
public sealed class SdkContractTests
{
    private string sdkDirectory = null!;
    private FieldInfo logDirectoryField = null!;
    private string? originalLogDirectory;
    private string testLogs = null!;

    [OneTimeSetUp]
    public void SetUpSdk()
    {
        sdkDirectory = typeof(SdkContractTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PluginApiDir").Value!;
        AssemblyLoadContext.Default.Resolving += ResolveAssembly;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
        var sdk = Assembly.LoadFrom(Path.Combine(sdkDirectory, "PluginApi.dll"));
        logDirectoryField = sdk.GetType("Loupedeck.PluginLogFile", true)!
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static).Single(field => field.FieldType == typeof(string));
        originalLogDirectory = (string?)logDirectoryField.GetValue(null);
        testLogs = Directory.CreateTempSubdirectory("ShortcutsSdkLogs-").FullName;
        logDirectoryField.SetValue(null, testLogs);
    }

    [OneTimeTearDown]
    public void TearDownSdk()
    {
        try
        {
            if (logDirectoryField is not null)
                logDirectoryField.SetValue(null, originalLogDirectory);
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveNative;
            // Skia finalizers may need native entry points after fixture teardown.
            if (!string.IsNullOrEmpty(testLogs) && Directory.Exists(testLogs))
                Directory.Delete(testLogs, true);
        }
    }

    private Assembly? ResolveAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        var path = new[] { sdkDirectory, Path.Combine(sdkDirectory, ".xamarin", architecture) }
            .Select(folder => Path.Combine(folder, name.Name + ".dll")).FirstOrDefault(File.Exists);
        return path is null ? null : context.LoadFromAssemblyPath(path);
    }

    private IntPtr ResolveNative(Assembly assembly, string name)
    {
        var path = Path.Combine(sdkDirectory, name.EndsWith(".dylib", StringComparison.Ordinal) ? name : name + ".dylib");
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }

    [Test]
    public void Discovery_PluginAssembly_ExportsOneMacCommandForBothDevices()
    {
        var actions = typeof(RunShortcutCommand).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(PluginAction).IsAssignableFrom(type)).ToArray();
        Assert.That(actions, Is.EqualTo(new[] { typeof(RunShortcutCommand) }));
        var command = Command();
        Assert.Multiple(() =>
        {
            Assert.That(command.IsDeviceSupported(DeviceType.Loupedeck70), Is.True);
            Assert.That(command.IsDeviceSupported(DeviceType.Loupedeck72), Is.True);
            Assert.That(command.IsOsSupported(LoupedeckOperatingSystem.Win), Is.False);
            Assert.That(command.IsOsSupported(LoupedeckOperatingSystem.Mac), Is.True);
        });
    }

    [Test]
    public void PluginConstruction_DotNet10_LoadsWithIsolatedLogging()
    {
        using var plugin = new MacShortcutsBridgePlugin();
        Assert.That(plugin.HasNoApplication && plugin.UsesApplicationApiOnly, Is.True);
    }

    [Test]
    public void RefreshConfigurationImages_ExistingError_PreservesStatusAndSupportDetails()
    {
        using var plugin = new MacShortcutsBridgePlugin();
        typeof(MacShortcutsBridgePlugin).GetField("loaded", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(plugin, true);
        plugin.OnPluginStatusChanged(Loupedeck.PluginStatus.Error,
            "Existing failure", "https://example.com/support", "Support");
        var original = plugin.PluginStatus;
        var refreshes = 0;
        plugin.PluginStatusChanged += (_, _) => refreshes++;

        plugin.RefreshConfigurationImages();

        Assert.Multiple(() =>
        {
            Assert.That(refreshes, Is.EqualTo(1));
            Assert.That(plugin.PluginStatus.Status, Is.EqualTo(original.Status));
            Assert.That(plugin.PluginStatus.Message, Is.EqualTo(original.Message));
            Assert.That(plugin.PluginStatus.SupportUrl, Is.EqualTo(original.SupportUrl));
            Assert.That(plugin.PluginStatus.SupportUrlTitle, Is.EqualTo(original.SupportUrlTitle));
        });
    }

    [Test]
    public void RefreshConfigurationImages_AfterUnload_DoesNotEmitStatus()
    {
        using var plugin = new MacShortcutsBridgePlugin();
        typeof(MacShortcutsBridgePlugin).GetField("loaded", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(plugin, true);
        plugin.OnPluginStatusChanged(Loupedeck.PluginStatus.Normal, "Ready");
        plugin.Unload();
        var refreshes = 0;
        plugin.PluginStatusChanged += (_, _) => refreshes++;
        plugin.RefreshConfigurationImages();
        Assert.That(refreshes, Is.Zero);
    }

    [Test]
    public void PluginStatusChanged_InvokesHostCallbacksOutsideGate()
    {
        using var plugin = new MacShortcutsBridgePlugin();
        typeof(MacShortcutsBridgePlugin).GetField("loaded", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(plugin, true);
        var gate = typeof(MacShortcutsBridgePlugin).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(plugin)!;
        var callbacksUnderGate = false;
        var callbacks = 0;
        plugin.PluginStatusChanged += (_, _) =>
        {
            callbacks++;
            callbacksUnderGate |= Monitor.IsEntered(gate);
        };

        plugin.ReportSuccess();
        plugin.ReportFailure("Failure");
        plugin.RefreshConfigurationImages();

        Assert.Multiple(() =>
        {
            Assert.That(callbacks, Is.EqualTo(3));
            Assert.That(callbacksUnderGate, Is.False);
        });
    }

    [Test]
    public void ControlsDeclared_OnlyShortcutIsVisibleAndNotRequired()
    {
        var command = Command();
        var controls = ((IEnumerable<ActionEditorControlBase>)typeof(ActionEditor)
            .GetMethod("GetControls", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(command.ActionEditor, null)!).ToArray();
        Assert.That(controls.Select(control => (control.Name, control.Type)), Is.EqualTo(new[]
        {
            ("shortcut", ActionEditorControlType.Listbox),
            ("shortcutname", ActionEditorControlType.Hidden),
            ("shortcuticon", ActionEditorControlType.Hidden)
        }));
        Assert.That(((ActionEditorListbox)controls[0]).IsRequired, Is.False);
        Assert.That(((ActionEditorListbox)controls[0]).Placeholder, Is.EqualTo("Search shortcuts..."));
        Assert.That(controls[0].GetType().GetProperty("Description")?.GetValue(controls[0]),
            Is.EqualTo("After selecting a shortcut, save the action to apply its icon to the preview. No manual icon change is needed."));
    }

    [TestCase("shortcut")]
    [TestCase("Shortcut")]
    [TestCase("SHORTCUT")]
    public async Task ListRequested_DuplicateNames_UsesIdentifiersAsValues(string controlName)
    {
        var first = new Shortcut(Guid.NewGuid(), "Same name");
        var second = new Shortcut(Guid.NewGuid(), "Same name");
        var command = await CreateCommand([first, second]);
        var args = new ActionEditorListboxItemsRequestedEventArgs(State(), controlName);
        Raise(command, args);
        Assert.That(args.Items, Has.Count.EqualTo(2));
        Assert.That(JsonSerializer.Serialize(args.Items), Does.Contain(first.Identifier).And.Contain(second.Identifier));
    }

    [Test]
    public async Task ListRequested_MissingSelection_PreservesUnavailableItem()
    {
        var command = await CreateCommand([]);
        var id = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var args = new ActionEditorListboxItemsRequestedEventArgs(State(id, "Old name"), "Shortcut");
        Raise(command, args);
        Assert.That(args.SelectedItemName, Is.EqualTo(id));
        Assert.That(JsonSerializer.Serialize(args.Items), Does.Contain("Old name (unavailable)"));
    }

    [TestCase("shortcut")]
    [TestCase("Shortcut")]
    public async Task SelectionChanged_ValidShortcut_InitializesSeparateNameAndIconReference(string controlName)
    {
        var shortcut = new Shortcut(Guid.NewGuid(), "My shortcut");
        var icons = new FakeIcons();
        var imageChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = await CreateCommand([shortcut], icons, name =>
        {
            if (name == "ActionImageChanged") { imageChanged.TrySetResult(); }
        });
        var state = State(shortcut.Identifier);
        Raise(command, new ActionEditorControlValueChangedEventArgs(state, controlName));
        Assert.Multiple(() =>
        {
            Assert.That(Messages(state).Any(message =>
                message.ControlName == "shortcutname" && message.MessageValue == shortcut.Name), Is.True);
            Assert.That(Messages(state).Any(message =>
                message.ControlName == "shortcuticon" && message.MessageValue.Length == 64), Is.True);
            Assert.That(Messages(state).Any(message =>
                message.ControlName == "shortcut" && message.MessageValue == "true"), Is.True);
            Assert.That(JsonSerializer.Serialize(Messages(state)), Does.Contain("My shortcut"));
        });
        await icons.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(icons.Identifier, Is.EqualTo(shortcut.Id));
        await imageChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Attach_Repeatedly_ReplacesCatalogSubscription()
    {
        var notifications = new List<string>();
        var command = Command(notifications.Add);
        var icons = new FakeIcons();
        var firstClient = new ShortcutsClient(new FakeProcesses());
        var firstCatalog = new ShortcutCatalog(firstClient);
        command.Attach(firstClient, firstCatalog, icons, (_, _) => { }, () => { });
        command.Attach(firstClient, firstCatalog, icons, (_, _) => { }, () => { });

        await firstCatalog.RefreshAsync();
        Assert.That(notifications, Is.EqualTo(new[] { "ListboxItemsChanged", "InvalidateControls" }));

        notifications.Clear();
        var secondClient = new ShortcutsClient(new FakeProcesses());
        var secondCatalog = new ShortcutCatalog(secondClient);
        command.Attach(secondClient, secondCatalog, icons, (_, _) => { }, () => { });
        await firstCatalog.RefreshAsync();
        Assert.That(notifications, Is.Empty);

        await secondCatalog.RefreshAsync();
        Assert.That(notifications, Is.EqualTo(new[] { "ListboxItemsChanged", "InvalidateControls" }));
    }

    [Test]
    public async Task ListRequested_WithoutStartedEvent_LoadsAndNotifiesHostWithoutRefreshLoop()
    {
        var shortcut = new Shortcut(Guid.NewGuid(), "Cold start");
        var processes = new FakeProcesses { ListCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var notifications = new List<string>();
        var command = Command(notifications.Add);
        var client = new ShortcutsClient(processes);
        var catalog = new ShortcutCatalog(client);
        command.Attach(client, catalog, new FakeIcons(), (_, _) => { }, () => { });

        var first = new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut");
        Raise(command, first);
        Assert.That(first.Items.Select(item => item.DisplayName), Is.EqualTo(new[] { "Loading shortcuts..." }));
        Assert.That(first.Items.Single().Name, Is.Empty);
        Assert.That(Messages(first.ActionEditorState).Any(message =>
            message.ControlName == "shortcut" && message.MessageValue == "false"), Is.True);

        var second = new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut");
        Raise(command, second);
        var pending = catalog.RefreshAsync();
        processes.ListCompletion.SetResult(new ProcessResult(0, $"{shortcut.Name} ({shortcut.Identifier})"));
        await pending;
        Assert.That(processes.ListCount, Is.EqualTo(1));
        Assert.That(notifications, Is.EqualTo(new[] { "ListboxItemsChanged", "InvalidateControls" }));

        var updated = new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut");
        Raise(command, updated);
        Assert.That(updated.Items.Single().Name, Is.EqualTo(shortcut.Identifier));
        Assert.That(processes.ListCount, Is.EqualTo(1), "The host's update request must not start discovery again.");
    }

    [Test]
    public async Task ListRequested_ReopenedPicker_QueriesAgainAndShowsNewShortcuts()
    {
        var processes = new FakeProcesses();
        var command = Command();
        var client = new ShortcutsClient(processes);
        var catalog = new ShortcutCatalog(client);
        command.Attach(client, catalog, new FakeIcons(), (_, _) => { }, () => { });
        await catalog.RefreshAsync();
        Raise(command, new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut"));
        var shortcut = new Shortcut(Guid.NewGuid(), "Added after opening");
        processes.Shortcuts = [shortcut];
        processes.ListCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var reopened = new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut");
        Raise(command, reopened);
        Assert.That(reopened.Items.Single().DisplayName, Is.EqualTo("Loading shortcuts..."));
        var pending = catalog.RefreshAsync();
        processes.ListCompletion.SetResult(new ProcessResult(0, $"{shortcut.Name} ({shortcut.Identifier})"));
        await pending;
        var updated = new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut");
        Raise(command, updated);
        Assert.That(updated.Items.Single().Name, Is.EqualTo(shortcut.Identifier));
        Assert.That(processes.ListCount, Is.EqualTo(2));
    }

    [TestCase("ALP", "Alpha")]
    [TestCase("café", "Café shortcut")]
    [TestCase("not present", "No matching shortcuts.")]
    public async Task ListRequested_SearchText_FiltersNamesWithoutAllowingArbitraryValues(string query, string expected)
    {
        var command = await CreateCommand([new(Guid.NewGuid(), "Alpha"), new(Guid.NewGuid(), "Café shortcut")]);
        var state = State(query);
        Raise(command, new ActionEditorControlValueChangedEventArgs(state, "shortcut"));
        var request = new ActionEditorListboxItemsRequestedEventArgs(state, "shortcut");
        Raise(command, request);
        Assert.That(request.Items.Single().DisplayName, Is.EqualTo(expected));
        Assert.That(Messages(state).Any(message =>
            message.ControlName == "shortcut" && message.MessageValue == "false"), Is.True);
    }

    [Test]
    public async Task ListRequested_EmptyCatalog_ShowsExplicitEmptyState()
    {
        var command = await CreateCommand([]);
        var request = new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut");
        Raise(command, request);
        Assert.That(request.Items.Single().DisplayName, Is.EqualTo("No shortcuts found."));
        Assert.That(request.Items.Single().Name, Is.Empty);
    }

    [Test]
    public async Task ListRequested_FailedDiscovery_ShowsErrorAndCanRetryOnReopen()
    {
        var processes = new FakeProcesses { ListCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var command = Command();
        var client = new ShortcutsClient(processes);
        var catalog = new ShortcutCatalog(client);
        var failures = new List<string>();
        command.Attach(client, catalog, new FakeIcons(), (message, _) => failures.Add(message), () => { });
        Raise(command, new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut"));
        var pending = catalog.RefreshAsync();
        processes.ListCompletion.SetResult(new ProcessResult(1, "private payload"));
        await pending;
        var failed = new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut");
        Raise(command, failed);
        Assert.That(failed.Items.Single().DisplayName,
            Does.Contain("Could not load shortcuts").And.Not.Contain("private").And.Not.Contain("Refresh"));
        Assert.That(failures, Has.Count.EqualTo(1));

        processes.ListCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Raise(command, new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut"));
        pending = catalog.RefreshAsync();
        processes.ListCompletion.SetResult(new ProcessResult(0, ""));
        await pending;
        var recovered = new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut");
        Raise(command, recovered);
        Assert.That(recovered.Items.Single().DisplayName, Is.EqualTo("No shortcuts found."));
        Assert.That(processes.ListCount, Is.EqualTo(2));
    }

    [Test]
    public async Task DiscoveryCompleted_AfterUnload_DoesNotNotifyHost()
    {
        var processes = new FakeProcesses { ListCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var notifications = new List<string>();
        var command = Command(notifications.Add);
        var client = new ShortcutsClient(processes);
        var catalog = new ShortcutCatalog(client);
        command.Attach(client, catalog, new FakeIcons(), (_, _) => Assert.Fail("Unexpected failure"), () => { });
        Raise(command, new ActionEditorListboxItemsRequestedEventArgs(State(), "shortcut"));
        var pending = catalog.RefreshAsync();
        typeof(RunShortcutCommand).GetMethod("OnUnload", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(command, null);
        processes.ListCompletion.SetResult(new ProcessResult(0, ""));
        await pending;
        Assert.That(notifications, Is.Empty);
    }

    [Test]
    public async Task SelectionChanged_IconCaptureFails_KeepsDefaultsWithoutWarning()
    {
        var shortcut = new Shortcut(Guid.NewGuid(), "No native icon");
        var icons = new FakeIcons { CaptureFailure = new IOException("Permission denied") };
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imageChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = Command(name =>
        {
            if (name == "ActionImageChanged") { imageChanged.TrySetResult(); }
            if (name == "InvalidateControls") { refreshed.TrySetResult(); }
        });
        var client = new ShortcutsClient(new FakeProcesses { Shortcuts = [shortcut] });
        var catalog = new ShortcutCatalog(client);
        command.Attach(client, catalog, icons, (_, _) => Assert.Fail("Optional icon failure must not change status"),
            () => Assert.Fail("Optional icon failure must not change status"));
        await catalog.RefreshAsync();
        refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = State(shortcut.Identifier);
        Raise(command, new ActionEditorControlValueChangedEventArgs(state, "shortcut"));
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await imageChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(Messages(state).Single(message => message.ControlName == "shortcutname").MessageValue,
            Is.EqualTo(shortcut.Name));
        var iconKey = Messages(state).Single(message => message.ControlName == "shortcuticon").MessageValue;
        using var selected = HostImage(command, new ActionEditorActionParameters(new Dictionary<string, string>
        {
            ["shortcuticon"] = iconKey
        }));
        using var fallback = HostImage(command, new ActionEditorActionParameters());
        Assert.That(selected.ToArray(), Is.EqualTo(fallback.ToArray()));
    }

    [Test]
    public async Task SelectionChanged_RapidReselection_KeepsIndependentSnapshotsWhenCaptureFinishesOutOfOrder()
    {
        var first = new Shortcut(Guid.NewGuid(), "First");
        var second = new Shortcut(Guid.NewGuid(), "Second");
        var firstStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editorRefreshes = 0;
        var icons = new FakeIcons
        {
            CaptureHandler = (identifier, key) =>
            {
                (identifier == first.Id ? firstStarted : secondStarted).TrySetResult(key);
                return (identifier == first.Id ? firstCompletion : secondCompletion).Task;
            }
        };
        var command = await CreateCommand([first, second], icons, name =>
        {
            if (name == "InvalidateControls" && Interlocked.Increment(ref editorRefreshes) == 2)
            {
                changed.TrySetResult();
            }
        });
        editorRefreshes = 0;
        var firstState = State(first.Identifier);
        Raise(command, new ActionEditorControlValueChangedEventArgs(firstState, "shortcut"));
        var secondState = State(second.Identifier);
        Raise(command, new ActionEditorControlValueChangedEventArgs(secondState, "shortcut"));
        var firstKey = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondKey = await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        secondCompletion.SetResult();
        firstCompletion.SetResult();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(firstKey, Is.Not.EqualTo(secondKey));
        Assert.That(Messages(secondState).Single(message => message.ControlName == "shortcuticon").MessageValue,
            Is.EqualTo(secondKey));
        Assert.That(Messages(secondState).Single(message => message.ControlName == "shortcutname").MessageValue,
            Is.EqualTo(second.Name));
    }

    [Test]
    public async Task SelectionChanged_WarmedIcon_SnapshotIsReadyBeforeImageRefreshWithoutRecapture()
    {
        var shortcut = new Shortcut(Guid.NewGuid(), "Cached native icon");
        using var native = new BitmapBuilder(90, 90);
        native.Clear(new BitmapColor(255, 0, 0));
        var store = new FakeIcons { Image = native.ToArray() };
        var directory = Directory.CreateTempSubdirectory("ShortcutSdkCache-").FullName;
        try
        {
            using var cache = new ShortcutIconCache(store, directory, _ => Assert.Fail());
            var state = State(shortcut.Identifier);
            var availableAtRefresh = false;
            var synchronizations = 0;
            var command = await CreateCommand([shortcut], cache, notification =>
            {
                if (notification == "ActionImageChanged")
                {
                    var key = Messages(state).Single(message => message.ControlName == "shortcuticon").MessageValue;
                    availableAtRefresh = cache.Read(key).SequenceEqual(native.ToArray());
                }
            }, () => synchronizations++);

            Raise(command, new ActionEditorControlValueChangedEventArgs(state, "shortcut"));

            Assert.Multiple(() =>
            {
                Assert.That(availableAtRefresh, Is.True);
                Assert.That(store.Captured.Task.IsCompleted, Is.False);
                Assert.That(synchronizations, Is.EqualTo(1));
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestCase("Loupedeck70")]
    [TestCase("Loupedeck72")]
    public async Task SelectionSaved_PackageTemplateDiffersFromHostDefault_AutomaticallyAppliesCachedNativeIcon(string device)
    {
        var directory = Directory.CreateTempSubdirectory("ShortcutSelectionSync-").FullName;
        try
        {
            var shortcut = new Shortcut(Guid.NewGuid(), "Selected native icon");
            using var native = new BitmapBuilder(90, 90);
            native.Clear(new BitmapColor(255, 0, 0));
            var png = native.ToArray();
            var store = new FakeIcons { Image = png };
            using var cache = new ShortcutIconCache(store, Path.Combine(directory, "Snapshots"), _ => Assert.Fail());
            var applications = Path.Combine(directory, "Applications");
            var profile = Path.Combine(applications, device, "@_defaultmac", "Profiles", "profile", "ProfileInfo.json");
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.WriteAllText(profile, """{"profileActions":[]}""");
            var template = DefaultActionIcon.Template;
            template["items"]![1]!["text"] = shortcut.Name;
            var hostDefaults = (JsonObject)template.DeepClone();
            using var renderedDefault = RenderHostTemplate(template, 90, 90);
            hostDefaults["items"]![0]!["image"] = Convert.ToBase64String(renderedDefault.ToArray());
            hostDefaults["items"]![0]!["area"] = new JsonObject
            { ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 100 };
            var host = new SelectionIconHost(template, hostDefaults);
            var synchronized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var synchronizer = new ShortcutIconTemplates(applications, Path.Combine(directory, "Ownership.json"),
                cache, host, [], png, exception => synchronized.TrySetException(exception),
                () => synchronized.TrySetResult(), RunShortcutCommand.PadShortcutIconForKeypad, template);
            var command = await CreateCommand([shortcut], cache, updateIconTemplates: synchronizer.QueueUpdate);
            var state = State(shortcut.Identifier);

            Raise(command, new ActionEditorControlValueChangedEventArgs(state, "shortcut"));
            var key = Messages(state).Single(message => message.ControlName == "shortcuticon").MessageValue;
            File.WriteAllText(profile, new JsonObject
            {
                ["profileActions"] = new JsonArray(new JsonObject
                {
                    ["name"] = "$@Generic___@ProfileAction___selection",
                    ["templateActionName"] = "$MacShortcutsBridge___RunShortcut",
                    ["actionParameters"] = new JsonObject
                    {
                        ["parameters"] = new JsonObject
                        {
                            ["shortcut"] = shortcut.Identifier,
                            ["shortcutname"] = shortcut.Name,
                            ["shortcuticon"] = key
                        }
                    }
                })
            }.ToJsonString());
            Raise(command, new ActionEditorFinishedEventArgs(false));
            await synchronized.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var expected = device == "Loupedeck70" ? RunShortcutCommand.PadShortcutIconForKeypad(png) : png;
            Assert.Multiple(() =>
            {
                Assert.That(cache.Read(key), Is.EqualTo(png));
                Assert.That(store.Captured.Task.IsCompleted, Is.False);
                Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
                    Is.EqualTo(Convert.ToBase64String(expected)));
                Assert.That(host.Current["items"]![1]!["text"]!.GetValue<string>(), Is.EqualTo(shortcut.Name));
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task SelectionChanged_CaptureCompletes_RefreshesHostWithNativeImage()
    {
        var shortcut = new Shortcut(Guid.NewGuid(), "Native icon");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imageRefreshes = 0;
        using var native = new BitmapBuilder(90, 90);
        native.Clear(new BitmapColor(255, 0, 0));
        var icons = new FakeIcons
        {
            CaptureHandler = async (_, _) => await completion.Task,
            ReadHandler = _ => completion.Task.IsCompleted ? native.ToArray() : throw new FileNotFoundException()
        };
        var command = await CreateCommand([shortcut], icons, name =>
        {
            if (name == "ActionImageChanged") { Interlocked.Increment(ref imageRefreshes); }
            if (name == "InvalidateControls") { refreshed.TrySetResult(); }
        }, () => synchronized.TrySetResult());
        refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = State(shortcut.Identifier);
        Raise(command, new ActionEditorControlValueChangedEventArgs(state, "shortcut"));
        var key = Messages(state).Single(message => message.ControlName == "shortcuticon").MessageValue;
        Assert.That(Volatile.Read(ref imageRefreshes), Is.Zero,
            "The host must not refresh the preview while the native icon is pending.");
        var parameters = new ActionEditorActionParameters(new Dictionary<string, string>
        {
            ["shortcut"] = shortcut.Identifier,
            ["shortcuticon"] = key
        });
        using var pending = HostImage(command, parameters);
        using var fallback = HostImage(command, new ActionEditorActionParameters());
        Assert.That(pending.ToArray(), Is.EqualTo(fallback.ToArray()));

        completion.SetResult();
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await synchronized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(Volatile.Read(ref imageRefreshes), Is.EqualTo(1));
        using var captured = HostImage(command, parameters);
        using var editorPreview = HostImage(command, new ActionEditorActionParameters());
        Assert.That(captured.ToArray(), Is.EqualTo(editorPreview.ToArray()));
    }

    [Test]
    public async Task CaptureIconAsync_PublishesRefreshOnCapturedContext()
    {
        var shortcut = new Shortcut(Guid.NewGuid(), "Context-bound icon");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var icons = new FakeIcons { CaptureHandler = async (_, _) => await completion.Task };
        var command = await CreateCommand([shortcut], icons, name =>
        {
            if (name == "ActionImageChanged") { refreshed.TrySetResult(SynchronizationContext.Current); }
        });
        var context = new CallbackSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var state = State(shortcut.Identifier);
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Raise(command, new ActionEditorControlValueChangedEventArgs(state, "shortcut"));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await icons.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        completion.SetResult();
        Assert.That(await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.SameAs(context));
    }

    [Test]
    public void PublishCapturedIcon_InvokesCallbacksOutsideGate()
    {
        object? gate = null;
        var hostCallbacks = new List<string>();
        var hostCallbacksUnderGate = new List<string>();
        var command = Command(name =>
        {
            if (name is not ("ActionImageChanged" or "InvalidateControls")) { return; }
            hostCallbacks.Add(name);
            if (gate is not null && Monitor.IsEntered(gate)) { hostCallbacksUnderGate.Add(name); }
        });
        gate = typeof(RunShortcutCommand).GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(command);
        var templateCallbackUnderGate = false;
        var client = new ShortcutsClient(new FakeProcesses());
        command.Attach(client, new ShortcutCatalog(client), new FakeIcons(), (_, _) => { }, () => { },
            () => templateCallbackUnderGate = Monitor.IsEntered(gate!));
        hostCallbacks.Clear();
        hostCallbacksUnderGate.Clear();

        typeof(RunShortcutCommand).GetMethod("PublishCapturedIcon", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(command, [1]);

        Assert.That(hostCallbacks, Does.Contain("ActionImageChanged"));
        Assert.That(hostCallbacks, Does.Contain("InvalidateControls"));
        Assert.That(hostCallbacksUnderGate, Is.Empty);
        Assert.That(templateCallbackUnderGate, Is.False);
    }

    [Test]
    public async Task CaptureIconAsync_AfterUnload_DoesNotNotifyHost()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new List<string>();
        var icons = new FakeIcons { CaptureHandler = (_, _) => completion.Task };
        var command = await CreateCommand([], icons, notifications.Add, () => notifications.Add("SyncIconTemplates"));
        notifications.Clear();
        var capture = (Task)typeof(RunShortcutCommand)
            .GetMethod("CaptureIconAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(command, [icons, Guid.NewGuid(), new string('A', 64)])!;
        await icons.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        typeof(RunShortcutCommand).GetMethod("OnUnload", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(command, null);
        completion.SetResult();
        await capture;
        Assert.That(notifications, Is.Empty);
    }

    [TestCase(false, 1)]
    [TestCase(true, 0)]
    public async Task EditorFinished_SaveOrCancel_OnlySaveSynchronizesTemplates(bool canceled, int expected)
    {
        var synchronizations = 0;
        var command = await CreateCommand([], updateIconTemplates: () => synchronizations++);
        Raise(command, new ActionEditorFinishedEventArgs(canceled));
        Assert.That(synchronizations, Is.EqualTo(expected));
    }

    [Test]
    public async Task EditorFinished_AfterUnload_DoesNotSynchronizeTemplates()
    {
        var synchronizations = 0;
        var command = await CreateCommand([], updateIconTemplates: () => synchronizations++);
        typeof(RunShortcutCommand).GetMethod("OnUnload", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(command, null);
        Raise(command, new ActionEditorFinishedEventArgs(false));
        Assert.That(synchronizations, Is.Zero);
    }

    [Test]
    public async Task EditorOpened_ExistingBinding_DoesNotRecaptureIconOrResetLabel()
    {
        var shortcut = new Shortcut(Guid.NewGuid(), "Renamed shortcut");
        var icons = new FakeIcons();
        var command = await CreateCommand([shortcut], icons);
        var state = State(shortcut.Identifier, "Original shortcut name", new string('A', 64));
        Raise(command, new ActionEditorStartedEventArgs(state));
        Assert.That(Messages(state).Any(message => message.ControlName is "shortcutname" or "shortcuticon"
            && message.MessageValue is not "false"), Is.False);
        Assert.That(icons.Captured.Task.IsCompleted, Is.False);
    }
    [Test]
    public async Task RunCommand_RepeatedPresses_StartsEveryRunWithoutWaiting()
    {
        var completion = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processes = new FakeProcesses { RunCompletion = completion };
        var client = new ShortcutsClient(processes);
        var catalog = new ShortcutCatalog(client);
        var command = Command();
        command.Attach(client, catalog, new FakeIcons(), (_, _) => { }, () => { });
        var method = typeof(RunShortcutCommand).GetMethod("RunCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var parameters = new ActionEditorActionParameters(new Dictionary<string, string>
        {
            ["Shortcut"] = Guid.NewGuid().ToString("D")
        });
        for (var i = 0; i < 12; i++) { Assert.That(method.Invoke(command, [parameters]), Is.True); }
        Assert.That(processes.RunCount, Is.EqualTo(12));
        completion.SetResult(new ProcessResult(0, ""));
        await command.RunAsync(client, Guid.NewGuid());
    }

    [Test]
    public async Task RunAsync_UnloadedAction_DoesNotPublishCompletionOrCancelInvocation()
    {
        var completion = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ShortcutsClient(new FakeProcesses { RunCompletion = completion });
        var command = Command();
        var notifications = 0;
        command.Attach(client, new ShortcutCatalog(client), new FakeIcons(),
            (_, _) => notifications++, () => notifications++);
        var run = command.RunAsync(client, Guid.NewGuid());
        typeof(RunShortcutCommand).GetMethod("OnUnload", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(command, null);
        Assert.That(run.IsCompleted, Is.False);
        completion.SetResult(new ProcessResult(0, ""));
        await run;
        Assert.That(notifications, Is.Zero);
    }

    [Test]
    public async Task CommandDisplayName_SnapshotName_UsesSeparateLabel()
    {
        var command = await CreateCommand([]);
        var method = typeof(RunShortcutCommand).GetMethod("GetCommandDisplayName", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var parameters = new ActionEditorActionParameters(new Dictionary<string, string>
        {
            ["ShortcutName"] = "Saved shortcut name"
        });
        Assert.That(method.Invoke(command, [parameters]), Is.EqualTo("Saved shortcut name"));
        Assert.That(method.Invoke(command, [new ActionEditorActionParameters()]), Is.EqualTo("Run Shortcut"));
    }

    [Test]
    public void CommandImage_InvalidCachedImage_UsesDefaultWithoutChangingStatus()
    {
        var client = new ShortcutsClient(new FakeProcesses());
        var command = Command();
        var notifications = 0;
        var icons = new FakeIcons { Image = [0, 1, 2, 3] };
        command.Attach(client, new ShortcutCatalog(client), icons, (_, _) => notifications++, () => notifications++);
        var parameters = new ActionEditorActionParameters(new Dictionary<string, string>
        {
            ["ShortcutIcon"] = new string('A', 64)
        });
        using var actual = HostImage(command, parameters);
        using var expected = HostImage(command, new ActionEditorActionParameters());
        Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()));
        Assert.That(notifications, Is.Zero);
        Assert.That(icons.Captured.Task.IsCompleted, Is.False);
    }

    [Test]
    public async Task CommandImage_MissingSavedSnapshot_RecoversCachedNativeIconWithoutReselection()
    {
        var directory = Directory.CreateTempSubdirectory("ShortcutSnapshotRecovery-").FullName;
        try
        {
            var shortcut = new Shortcut(Guid.NewGuid(), "Recovered native icon");
            using var native = new BitmapBuilder(90, 90);
            native.Clear(new BitmapColor(255, 0, 0));
            var store = new FakeIcons { Image = native.ToArray() };
            using var cache = new ShortcutIconCache(store, directory, _ => Assert.Fail());
            var command = await CreateCommand([shortcut], cache);
            var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var parameters = new ActionEditorActionParameters(new Dictionary<string, string>
            {
                ["shortcut"] = shortcut.Identifier,
                ["shortcutname"] = shortcut.Name,
                ["shortcuticon"] = key
            });
            Assert.That(File.Exists(Path.Combine(directory, key + ".png")), Is.False);

            using var actual = HostImage(command, parameters);
            using var expected = HostImage(await CreateCommand([shortcut], store), parameters);

            Assert.Multiple(() =>
            {
                Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()));
                Assert.That(cache.Read(key), Is.EqualTo(native.ToArray()));
                Assert.That(store.Captured.Task.IsCompleted, Is.False);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void CommandImage_ValidCachedImage_UsesNativeSnapshot()
    {
        using var native = new BitmapBuilder(20, 20);
        native.Clear(new BitmapColor(255, 0, 0));
        var client = new ShortcutsClient(new FakeProcesses());
        var command = Command();
        var icons = new FakeIcons { Image = native.ToArray() };
        command.Attach(client, new ShortcutCatalog(client), icons, (_, _) => { }, () => { });
        var parameters = new ActionEditorActionParameters(new Dictionary<string, string>
        {
            ["ShortcutIcon"] = new string('B', 64)
        });
        using var actual = HostImage(command, parameters);
        using var fullCanvas = new BitmapBuilder(actual.Width, actual.Height);
        fullCanvas.Clear(new BitmapColor(255, 0, 0));
        using var blankCanvas = new BitmapBuilder(actual.Width, actual.Height);
        blankCanvas.Clear(BitmapColor.Transparent);
        Assert.That(actual.ToArray(), Is.Not.EqualTo(fullCanvas.ToArray()),
            "The native image should be inset rather than touching the canvas edges.");
        Assert.That(actual.ToArray(), Is.Not.EqualTo(blankCanvas.ToArray()),
            "The native image should remain visible inside its inset.");
        Assert.That(icons.Captured.Task.IsCompleted, Is.False);
    }

    [Test]
    public async Task CommandImage_UnsavedEditorSelection_UsesSelectedIconForPreview()
    {
        var shortcut = new Shortcut(Guid.NewGuid(), "Preview shortcut");
        using var native = new BitmapBuilder(20, 20);
        native.Clear(new BitmapColor(255, 0, 0));
        var icons = new FakeIcons { Image = native.ToArray() };
        var command = await CreateCommand([shortcut], icons);
        var state = State(shortcut.Identifier);
        Raise(command, new ActionEditorControlValueChangedEventArgs(state, "shortcut"));
        var iconKey = Messages(state).Single(message => message.ControlName == "shortcuticon").MessageValue;

        using var preview = HostImage(command, new ActionEditorActionParameters());
        using var selected = HostImage(command, new ActionEditorActionParameters(new Dictionary<string, string>
        {
            ["shortcuticon"] = iconKey
        }));
        using var defaultImage = HostImage(Command(), new ActionEditorActionParameters());
        Assert.Multiple(() =>
        {
            Assert.That(preview.ToArray(), Is.EqualTo(selected.ToArray()));
            Assert.That(preview.ToArray(), Is.Not.EqualTo(defaultImage.ToArray()));
        });
    }

    [TestCase(50)]
    [TestCase(90)]
    [TestCase(116)]
    [TestCase(256)]
    public void DefaultIcon_RendersFullTemplateAtRequestedSize(int size)
    {
        var command = new RunShortcutCommand();
        var collection = new ActionEditorCommandCollection();
        collection.AddAction(command);
        var method = typeof(RunShortcutCommand).GetMethod("GetCommandImage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var image = (BitmapImage)method.Invoke(command, [new ActionEditorActionParameters(), size, size])!;
        Assert.That((image.Width, image.Height), Is.EqualTo((size, size)));
        Assert.That(image.ToArray().Length, Is.GreaterThan(100));
        if (size == 256)
        {
            var artwork = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Artwork");
            Directory.CreateDirectory(artwork);
            File.WriteAllBytes(Path.Combine(artwork, "Icon256x256.png"), image.ToArray());
        }
    }

    [TestCase(PluginImageSize.Width60, false)]
    [TestCase(PluginImageSize.Width90, false)]
    [TestCase(PluginImageSize.Width116, false)]
    [TestCase(PluginImageSize.Width90, true)]
    public void DefaultIcon_HostDispatch_MatchesPackagedTemplate(PluginImageSize size, bool selected)
    {
        var path = typeof(SdkContractTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PackageDirectory").Value!;
        var packaged = JsonNode.Parse(File.ReadAllText(Path.Combine(path, "metadata", "DefaultIconTemplate.ict")))!.AsObject();
        var command = Command();
        var parameters = new ActionEditorActionParameters(selected ? new Dictionary<string, string>
        {
            ["shortcutname"] = "Selected shortcut"
        } : []);
        using var actual = HostImage(command, parameters, size);
        using var expected = RenderHostTemplate(packaged, actual.Width, actual.Height,
            selected ? "Selected shortcut" : null);

        Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()));
    }

    [Test]
    public void DefaultIcon_TemplateAccess_ReturnsIndependentNestedCopies()
    {
        var original = DefaultActionIcon.Template;
        var changed = DefaultActionIcon.Template;
        changed["items"]![0]!["area"]!["width"] = 1;
        changed["items"]![1]!["text"] = "Changed";

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.Not.SameAs(original));
            Assert.That(JsonNode.DeepEquals(changed, original), Is.False);
            Assert.That(JsonNode.DeepEquals(DefaultActionIcon.Template, original), Is.True);
        });
    }

    [Test]
    public void DefaultIcon_NamedRender_DoesNotChangeLaterRendersOrTemplate()
    {
        var template = DefaultActionIcon.Template;
        using var before = DefaultActionIcon.Render(90, 90);
        using var named = DefaultActionIcon.Render(90, 90, "Selected shortcut");
        using var after = DefaultActionIcon.Render(90, 90);

        Assert.Multiple(() =>
        {
            Assert.That(after.ToArray(), Is.EqualTo(before.ToArray()));
            Assert.That(named.ToArray(), Is.Not.EqualTo(before.ToArray()));
            Assert.That(JsonNode.DeepEquals(DefaultActionIcon.Template, template), Is.True);
        });
    }

    [Test]
    public void DefaultIcon_ReplacementTemplate_UsesArtworkLayoutBackgroundAndTextStyles()
    {
        var template = DefaultActionIcon.Template;
        using var artwork = new BitmapBuilder(30, 30);
        artwork.Clear(new BitmapColor(255, 0, 0));
        template["backgroundColor"] = 4278190335;
        template["items"]![0]!["image"] = Convert.ToBase64String(artwork.ToArray());
        template["items"]![0]!["area"] = new JsonObject
        { ["x"] = 5, ["y"] = 8, ["width"] = 35, ["height"] = 45 };
        template["items"]![1]!["textColor"] = 4278255360;
        template["items"]![1]!["fontSize"] = 10;
        template["items"]![1]!["text"] = "Replacement";
        using var actual = DefaultActionIcon.Render(template, 116, 116);
        using var expected = RenderHostTemplate(template, 116, 116);
        using var original = DefaultActionIcon.Render(116, 116);
        Assert.Multiple(() =>
        {
            Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()));
            Assert.That(actual.ToArray(), Is.Not.EqualTo(original.ToArray()));
        });
    }

    [Test]
    public void DefaultIcon_InvalidTemplate_ReportsExplicitFailure()
    {
        Assert.Throws<IOException>(() => DefaultActionIcon.Render(
            new JsonObject { ["items"] = "invalid" }, 90, 90));
    }

    private BitmapImage RenderHostTemplate(JsonObject template, int width, int height, string? name = null)
    {
        var shared = Assembly.LoadFrom(Path.Combine(sdkDirectory, "LoupedeckShared.dll"));
        var iconType = shared.GetType("Loupedeck.Service.ActionIcon", true)!;
        object?[] parseArgs = [template.ToJsonString(), null];
        Assert.That(iconType.GetMethod("TryParse")!.Invoke(null, parseArgs), Is.True);
        if (name is not null) { iconType.GetMethod("SetText")!.Invoke(parseArgs[1], [name]); }
        var method = shared.GetType("Loupedeck.Service.ActionIconBuilder", true)!.GetMethods()
            .Single(candidate => candidate.Name == "CreateImage" && candidate.GetParameters().Length == 4);
        var flags = Enum.ToObject(method.GetParameters()[3].ParameterType, 0);
        return (BitmapImage)method.Invoke(null, [width, height, parseArgs[1], flags])!;
    }

    [Test]
    public void ShortcutIcon_UsesDefaultTemplateImageArea()
    {
        var sourceBuilder = new BitmapBuilder(100, 100);
        sourceBuilder.Clear(new BitmapColor(255, 0, 0));
        var source = sourceBuilder.ToImage();
        using var expectedBuilder = new BitmapBuilder(100, 100);
        expectedBuilder.Clear(BitmapColor.Transparent);
        expectedBuilder.DrawImage(source, 21, 14, 58, 58);
        using var expectedImage = expectedBuilder.ToImage();
        var expected = expectedImage.ToArray();
        var resize = typeof(RunShortcutCommand).GetMethod("Resize", BindingFlags.NonPublic | BindingFlags.Static)!;

        using var actual = (BitmapImage)resize.Invoke(null, [source, 100, 100])!;
        Assert.That(actual.ToArray(), Is.EqualTo(expected));
    }

    [Test]
    public void ShortcutIcon_KeypadPadding_FitsArtworkInCenteredCanvas()
    {
        using var sourceBuilder = new BitmapBuilder(20, 20);
        sourceBuilder.Clear(new BitmapColor(255, 0, 0));
        using var source = sourceBuilder.ToImage();
        using var expectedBuilder = new BitmapBuilder(256, 256);
        expectedBuilder.Clear(BitmapColor.Transparent);
        expectedBuilder.DrawImage(source, 64, 51, 128, 128);
        using var expected = expectedBuilder.ToImage();

        var formatted = RunShortcutCommand.PadShortcutIconForKeypad(source.ToArray());
        Assert.That(BitmapImage.TryCreateFromArray(formatted, out var actual), Is.True);
        using (actual)
        {
            Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()));
        }
    }

    [Test]
    public void ShortcutIcon_KeypadPadding_ThrowsForUndecodableBytes()
    {
        var exception = Assert.Throws<IOException>(() =>
            RunShortcutCommand.PadShortcutIconForKeypad([]));

        Assert.That(exception!.Message, Is.EqualTo("The shortcut icon could not be decoded."));
    }

    [TestCase(PluginImageSize.Width60)]
    [TestCase(PluginImageSize.Width90)]
    [TestCase(PluginImageSize.Width116)]
    public void CommandImage_HostDispatch_ReturnsColorDefault(PluginImageSize size)
    {
        var command = Command();
        using var image = HostImage(command, new ActionEditorActionParameters(), size);
        var method = typeof(RunShortcutCommand).GetMethod("GetCommandImage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var expected = (BitmapImage)method.Invoke(command,
            [new ActionEditorActionParameters(), image.Width, image.Height])!;
        Assert.That(image.ToArray(), Is.EqualTo(expected.ToArray()));
    }

    [Test]
    public void Package_MetadataAndTemplate_AreMacOnlyWithEditableImageAndText()
    {
        var path = typeof(SdkContractTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PackageDirectory").Value!;
        var manifest = File.ReadAllText(Path.Combine(path, "metadata", "LoupedeckPackage.yaml"));
        Assert.That(manifest, Does.Contain("pluginFolderMac: bin").And.Not.Contain("pluginFolderWin"));
        Assert.That(manifest, Does.Contain("Loupedeck70").And.Contain("Loupedeck72"));
        Assert.That(Directory.GetFiles(Path.Combine(path, "bin")), Has.None.EndsWith("PluginApi.dll"));
        Assert.That(Directory.GetFiles(Path.Combine(path, "bin")), Has.None.EndsWith("LoupedeckShared.dll"));
        Assert.That(File.Exists(Path.Combine(path, "actionicons", "RunShortcut.svg")), Is.False);
        Assert.That(File.Exists(Path.Combine(path, "actionsymbols", "RunShortcut.svg")), Is.True);
        var template = File.ReadAllText(Path.Combine(path, "metadata", "DefaultIconTemplate.ict"));
        Assert.That(File.ReadAllText(Path.Combine(path, "icontemplates", "RunShortcut.ict")), Is.EqualTo(template),
            "The host's per-action ICT must use the same definition as metadata and runtime rendering.");
        using var json = JsonDocument.Parse(template);
        Assert.That(json.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("itemType").GetString()), Is.EqualTo(new[] { "Image", "Text" }));
    }

    [Test]
    public void Package_DefaultTemplate_ContainsUntintedColorArtworkAndAllowsNativeReplacement()
    {
        var path = typeof(SdkContractTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PackageDirectory").Value!;
        var template = File.ReadAllText(Path.Combine(path, "metadata", "DefaultIconTemplate.ict"));
        using var json = JsonDocument.Parse(template);
        var imageItem = json.RootElement.GetProperty("items")[0];
        Assert.That(Convert.FromBase64String(imageItem.GetProperty("image").GetString()!),
            Is.EqualTo(File.ReadAllBytes(Path.Combine(path, "metadata", "Icon256x256.png"))));
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(template), DefaultActionIcon.Template), Is.True,
            "The packaged and runtime defaults must consume the same ICT definition.");

        var shared = Assembly.LoadFrom(Path.Combine(sdkDirectory, "LoupedeckShared.dll"));
        var iconType = shared.GetType("Loupedeck.Service.ActionIcon", true)!;
        object?[] parseArgs = [template, null];
        Assert.That(iconType.GetMethod("TryParse")!.Invoke(null, parseArgs), Is.True);
        var icon = parseArgs[1]!;
        var builderType = shared.GetType("Loupedeck.Service.ActionIconBuilder", true)!;
        var createImage = builderType.GetMethods().Single(method => method.Name == "CreateImage"
            && method.GetParameters().Length == 3);
        var flags = Enum.ToObject(createImage.GetParameters()[2].ParameterType, 0);
        using var original = (BitmapImage)createImage.Invoke(null,
            [PluginImageSize.Width90, icon, flags])!;
        using var blank = new BitmapBuilder(original.Width, original.Height);
        blank.Clear(new BitmapColor(0, 0, 0));
        Assert.That(original.ToArray(), Is.Not.EqualTo(blank.ToArray()),
            "The packaged template must render an image without a command callback.");

        using var native = new BitmapBuilder(90, 90);
        native.Clear(new BitmapColor(255, 0, 0));
        iconType.GetMethod("SetImage", [typeof(byte[])])!.Invoke(icon, [native.ToArray()]);
        using var replaced = (BitmapImage)createImage.Invoke(null,
            [PluginImageSize.Width90, icon, flags])!;
        Assert.That(replaced.ToArray(), Is.Not.EqualTo(original.ToArray()));
        var artwork = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Artwork");
        Directory.CreateDirectory(artwork);
        File.WriteAllBytes(Path.Combine(artwork, "DefaultTemplate.png"), original.ToArray());
    }

    [Test]
    public void Package_ActionSymbol_ResolvesFromPackagedOriginalArtwork()
    {
        var path = typeof(SdkContractTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PackageDirectory").Value!;
        var package = new PluginPackage();
        typeof(PluginPackage).GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(package, [path]);
        Assert.That(package.TryGetActionSymbolBytes("RunShortcut", "", out var symbol), Is.True);
        Assert.That(symbol, Has.Length.GreaterThan(100));
        using var colorStream = typeof(RunShortcutCommand).Assembly.GetManifestResourceStream(
            "Loupedeck.MacShortcutsBridgePlugin.Assets.ShortcutsBridge.svg")!;
        using var reader = new StreamReader(colorStream);
        Assert.That(System.Text.Encoding.UTF8.GetString(symbol), Is.EqualTo(reader.ReadToEnd()));
        Assert.That(package.TryGetActionIconBytes(new ActionString("MacShortcutsBridge", "RunShortcut", ""),
            out _), Is.False, "A static image must not override the ICT.");
        Assert.That(package.ActionIconTemplatesDirectoryPath, Is.EqualTo(Path.Combine(path, "icontemplates")));
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(
            package.ActionIconTemplatesDirectoryPath, "RunShortcut.ict")))!.AsObject();
        using var rendered = RenderHostTemplate(template, 90, 90);
        using var expected = DefaultActionIcon.Render(90, 90);
        Assert.That(rendered.ToArray(), Is.EqualTo(expected.ToArray()));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Package_ActionSymbol_HostAndFrontendNormalizationPreservesPluginColors(bool darkTheme)
    {
        var path = typeof(SdkContractTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PackageDirectory").Value!;
        var package = new PluginPackage();
        typeof(PluginPackage).GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(package, [path]);
        Assert.That(package.TryGetActionSymbolBitmap("RunShortcut", "", out var symbol), Is.True);
        using (symbol)
        {
            var vector = new VectorImage();
            Assert.That(vector.Load(symbol), Is.True);
            vector.ReplaceForegroundColor(BitmapColor.Black);
            // Options+ recolors all fill attributes, including gradient references.
            var frontendSvg = Regex.Replace(vector.ToString(), "fill=\"[^\"]*\"",
                $"fill=\"{(darkTheme ? "#ffffff" : "#000000")}\"");
            var frontendVector = new VectorImage();
            Assert.That(frontendVector.Load(frontendSvg), Is.True);
            using var rendered = frontendVector.ToBitmap(256, 256);
            Assert.That(rendered.ToArray(), Is.EqualTo(File.ReadAllBytes(
                Path.Combine(path, "metadata", "Icon256x256.png"))));
        }
    }

    [Test]
    public void Artwork_ColorSource_UsesTwoGradientLayersWithoutCheckmark()
    {
        var assembly = typeof(RunShortcutCommand).Assembly;
        using var coloredStream = assembly.GetManifestResourceStream(
            "Loupedeck.MacShortcutsBridgePlugin.Assets.ShortcutsBridge.svg")!;
        var colored = XDocument.Load(coloredStream);
        XNamespace svg = "http://www.w3.org/2000/svg";
        var layers = colored.Root!.Elements(svg + "path").ToArray();
        Assert.That(layers, Has.Length.EqualTo(2));
        Assert.That(layers.Select(path => (string?)path.Attribute("style")),
            Is.EqualTo(new[] { "fill:url(#lower)", "fill:url(#upper)" }));
        using var rendered = BitmapImage.FromResource(assembly,
            "Loupedeck.MacShortcutsBridgePlugin.Assets.ShortcutsBridge.svg");
        using var builder = new BitmapBuilder(256, 256);
        builder.Clear(BitmapColor.Transparent);
        builder.DrawImage(rendered, 0, 0, 256, 256);
        var bytes = builder.ToArray();
        Assert.That(bytes.Length, Is.GreaterThan(100));
        var artwork = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Artwork");
        Directory.CreateDirectory(artwork);
        File.WriteAllBytes(Path.Combine(artwork, "RunShortcut.png"), bytes);
    }

    private static ActionEditorState State(string id = "", string name = "", string icon = "") => new(
    [
        new() { Name = "Shortcut", Value = id },
        new() { Name = "ShortcutName", Value = name },
        new() { Name = "ShortcutIcon", Value = icon }
    ]);

    private static BitmapImage HostImage(RunShortcutCommand command, ActionEditorActionParameters parameters,
        PluginImageSize size = PluginImageSize.Width90)
    {
        var collection = new ActionEditorCommandCollection();
        collection.AddAction(command);
        Assert.That(collection.TryGetCommandImage(command.Name, parameters, size, out var image), Is.True);
        return image;
    }

    private static void Raise<T>(RunShortcutCommand command, T args) where T : EventArgs
    {
        var field = typeof(ActionEditor).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(field => field.FieldType == typeof(EventHandler<T>));
        ((EventHandler<T>?)field.GetValue(command.ActionEditor))?.Invoke(command.ActionEditor, args);
    }

    private static IReadOnlyList<ActionEditorMessage> Messages(ActionEditorState state) =>
        (IReadOnlyList<ActionEditorMessage>)typeof(ActionEditorState)
            .GetProperty("Messages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(state)!;

    private static async Task<RunShortcutCommand> CreateCommand(IReadOnlyList<Shortcut> shortcuts, IShortcutIcons? icons = null,
        Action<string>? notification = null, Action? updateIconTemplates = null)
    {
        var processes = new FakeProcesses { Shortcuts = shortcuts };
        var client = new ShortcutsClient(processes);
        var catalog = new ShortcutCatalog(client);
        var command = Command(notification);
        command.Attach(client, catalog, icons ?? new FakeIcons(), (_, _) => { }, () => { }, updateIconTemplates);
        await catalog.RefreshAsync();
        return command;
    }

    private static RunShortcutCommand Command(Action<string>? notification = null)
    {
        var command = new RunShortcutCommand();
        var initialize = typeof(ActionEditor).GetMethod("Initialize", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var host = DispatchProxy.Create(initialize.GetParameters()[1].ParameterType, typeof(EditorHost));
        ((EditorHost)host).Notification = notification;
        initialize.Invoke(command.ActionEditor, ["RunShortcut", host]);
        typeof(ActionEditorAction).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(field => field.FieldType == initialize.GetParameters()[1].ParameterType).SetValue(command, host);
        return command;
    }

    public class EditorHost : DispatchProxy
    {
        public Action<string>? Notification { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Notification?.Invoke(targetMethod!.Name);
            return null;
        }
    }

    private sealed class CallbackSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try { callback(state); }
                finally { SetSynchronizationContext(previous); }
            });
        }
    }

    private sealed class FakeProcesses : IProcessRunner
    {
        public IReadOnlyList<Shortcut> Shortcuts { get; set; } = [];
        public TaskCompletionSource<ProcessResult>? ListCompletion { get; set; }
        public TaskCompletionSource<ProcessResult>? RunCompletion { get; init; }
        public int RunCount;
        public int ListCount;

        public Task<ProcessResult> RunAsync(ProcessRequest request)
        {
            if (request.Arguments[0] == "list")
            {
                Interlocked.Increment(ref ListCount);
                return ListCompletion?.Task ?? Task.FromResult(new ProcessResult(0,
                    string.Join("\n", Shortcuts.Select(item => $"{item.Name} ({item.Identifier})"))));
            }
            Interlocked.Increment(ref RunCount);
            return RunCompletion?.Task ?? Task.FromResult(new ProcessResult(0, ""));
        }
    }

    private sealed class FakeIcons : IShortcutIcons
    {
        public Guid Identifier;
        public byte[]? Image { get; init; }
        public Exception? CaptureFailure { get; init; }
        public Func<Guid, string, Task>? CaptureHandler { get; init; }
        public Func<string, byte[]>? ReadHandler { get; init; }
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CaptureAsync(Guid identifier, string key)
        {
            Identifier = identifier;
            Captured.TrySetResult();
            if (CaptureFailure is { } error) { throw error; }
            return CaptureHandler?.Invoke(identifier, key) ?? Task.CompletedTask;
        }
        public byte[] Read(string key) => ReadHandler?.Invoke(key) ?? Image ?? throw new FileNotFoundException();
    }

    private sealed class SelectionIconHost(JsonObject current, JsonObject defaults) : IActionIconHost
    {
        public JsonObject Current { get; private set; } = (JsonObject)current.DeepClone();

        public Task<JsonObject> GetAsync(ShortcutIconBinding binding, bool getDefaults, CancellationToken cancellationToken) =>
            Task.FromResult((JsonObject)(getDefaults ? defaults : Current).DeepClone());

        public Task SetAsync(ShortcutIconBinding binding, JsonObject icon, CancellationToken cancellationToken)
        {
            Current = (JsonObject)icon.DeepClone();
            return Task.CompletedTask;
        }
    }
}
