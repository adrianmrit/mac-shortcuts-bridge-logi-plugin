using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MacShortcutsBridge.Core;

namespace MacShortcutsBridge.Core.Tests;

[TestFixture]
public sealed class ShortcutIconTemplatesTests
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string NextKey = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string ActionName = "$@Generic___@ProfileAction___123456";
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes("<svg><path style=\"fill:url(#upper)\"/></svg>");
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jEioAAAAASUVORK5CYII=");
    private string directory = null!;
    private string applications = null!;
    private string profile = null!;
    private string ownership = null!;
    private FakeHost host = null!;
    private FakeIcons icons = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Directory.CreateTempSubdirectory("ShortcutTemplateTests-").FullName;
        applications = Path.Combine(directory, "Applications");
        profile = Path.Combine(applications, "Loupedeck70", "@_defaultmac", "Profiles", "profile", "ProfileInfo.json");
        ownership = Path.Combine(directory, "PluginData", "ownership.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        SaveBinding(Key);
        host = new FakeHost();
        icons = new FakeIcons();
        icons.Images[Key] = [.. Png, 1];
        icons.Images[NextKey] = [.. Png, 2];
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    [Test]
    public async Task Synchronize_SavedIcon_RefreshesConfigurationAfterImageAndOwnershipAreConfirmed()
    {
        var refreshes = 0;
        using var synchronizer = Create(() =>
        {
            Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
                Is.EqualTo(Convert.ToBase64String(icons.Images[Key])));
            Assert.That(File.Exists(ownership), Is.True);
            refreshes++;
        });
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(refreshes, Is.EqualTo(1));
    }

    [Test]
    public async Task Synchronize_NativeUpdate_PreservesHostAndOwnershipTransactionOrder()
    {
        host.AfterSet = () =>
        {
            var pending = JsonNode.Parse(File.ReadAllText(ownership))!.AsObject().Single().Value!.GetValue<string>();
            Assert.That(pending.Split(':'), Has.Length.EqualTo(4),
                "Both ownership states must be persisted before the host write.");
        };
        using var synchronizer = Create();

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.That(host.Requests, Is.EqualTo(new[] { "current", "defaults", "current", "set", "current" }));
        var confirmed = JsonNode.Parse(File.ReadAllText(ownership))!.AsObject().Single().Value!.GetValue<string>();
        Assert.That(confirmed.Split(':'), Has.Length.EqualTo(2));
        host.Requests.Clear();

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.That(host.Requests, Is.EqualTo(new[] { "current", "defaults" }),
            "An unchanged owned icon must not introduce extra requests or writes.");
    }

    [Test]
    public async Task Synchronize_AlreadyCorrectImage_StillInvalidatesStaleConfigurationCache()
    {
        var refreshes = 0;
        using var synchronizer = Create(() => refreshes++);
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(host.Writes, Is.EqualTo(1));
            Assert.That(refreshes, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Synchronize_BothDevices_RefreshesConfigurationOnceAfterAllBindings()
    {
        var secondProfile = Path.Combine(applications, "Loupedeck72", "@_defaultmac",
            "Profiles", "profile", "ProfileInfo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(secondProfile)!);
        File.Copy(profile, secondProfile);
        var refreshes = 0;
        using var synchronizer = Create(() =>
        {
            Assert.That(host.ReadDevices, Is.EquivalentTo(new[] { "Loupedeck70", "Loupedeck72" }));
            refreshes++;
        });
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(refreshes, Is.EqualTo(1));
    }

    [Test]
    public async Task Synchronize_UnrelatedActions_DoesNotRefreshConfiguration()
    {
        var saved = JsonNode.Parse(File.ReadAllText(profile))!;
        saved["profileActions"]![0]!["templateActionName"] = "$AnotherPlugin___RunShortcut";
        File.WriteAllText(profile, saved.ToJsonString());
        var refreshes = 0;
        using var synchronizer = Create(() => refreshes++);
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(refreshes, Is.Zero);
    }

    [Test]
    public void Synchronize_FailedSave_DoesNotSendSuccessRefresh()
    {
        File.WriteAllText(Path.Combine(directory, "PluginData"), "Not a directory.");
        var refreshes = 0;
        using var synchronizer = Create(() => refreshes++);
        Assert.ThrowsAsync<IOException>(() => synchronizer.SynchronizeAsync(CancellationToken.None));
        Assert.That(refreshes, Is.Zero);
    }

    [Test]
    public void Synchronize_Cancellation_DoesNotRefreshConfiguration()
    {
        var refreshes = 0;
        using var synchronizer = Create(() => refreshes++);
        Assert.ThrowsAsync<OperationCanceledException>(() =>
            synchronizer.SynchronizeAsync(new CancellationToken(true)));
        Assert.That(refreshes, Is.Zero);
    }

    [Test]
    public void Synchronize_CancellationDuringWrite_DoesNotRefreshConfiguration()
    {
        using var cancellation = new CancellationTokenSource();
        host.AfterSet = cancellation.Cancel;
        var refreshes = 0;
        using var synchronizer = Create(() => refreshes++);
        Assert.ThrowsAsync<OperationCanceledException>(() => synchronizer.SynchronizeAsync(cancellation.Token));
        Assert.That(refreshes, Is.Zero);
    }

    [Test]
    public async Task Synchronize_DefaultTemplate_UpdatesOnlyImageAndPersistsOwnership()
    {
        var before = (JsonObject)host.Current.DeepClone();
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        before["items"]![0]!["image"] = Convert.ToBase64String(icons.Images[Key]);
        Assert.Multiple(() =>
        {
            Assert.That(JsonNode.DeepEquals(host.Current, before), Is.True);
            Assert.That(host.Writes, Is.EqualTo(1));
            Assert.That(File.Exists(ownership), Is.True);
        });
    }

    [Test]
    public async Task Synchronize_LegacyFillAttributeArtwork_RecognizesOldDefault()
    {
        host.Current["items"]![0]!["image"] = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("<svg><path fill=\"url(#upper)\"/></svg>"));
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.EqualTo(1));
    }

    [Test]
    public async Task Synchronize_LegacyStockTextOnlyTemplate_RestoresTemplateDefault()
    {
        host.Current["items"]!.AsArray().RemoveAt(0);
        host.Current["items"]![0]!["area"] = new JsonObject
        { ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 100 };
        icons.Images.Clear();
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(host.Current["items"]!.AsArray(), Has.Count.EqualTo(2));
            Assert.That(JsonNode.DeepEquals(host.Current, host.Defaults), Is.True);
        });
    }

    [Test]
    public async Task Synchronize_CustomTextOnlyTemplate_PreservesCustomization()
    {
        host.Current["items"]!.AsArray().RemoveAt(0);
        host.Current["items"]![0]!["area"] = new JsonObject
        { ["x"] = 5, ["y"] = 0, ["width"] = 95, ["height"] = 100 };
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.Zero);
    }

    [Test]
    public async Task Synchronize_ManualImage_PreservesCustomization()
    {
        host.Current["items"]![0]!["image"] = Convert.ToBase64String([.. Png, 7]);
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.Zero);
    }

    [TestCase("text")]
    [TestCase("layout")]
    public async Task Synchronize_ManualTextOrLayout_PreservesCustomization(string customization)
    {
        if (customization == "text") { host.Current["items"]![1]!["text"] = "My custom label"; }
        else { host.Current["items"]![0]!["area"]!["x"] = 20; }
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.Zero);
    }

    [Test]
    public async Task Synchronize_NewSelectionAfterReload_UpdatesPreviouslyOwnedNativeImage()
    {
        using (var first = Create()) { await first.SynchronizeAsync(CancellationToken.None); }
        SaveBinding(NextKey);
        using (var reloaded = Create()) { await reloaded.SynchronizeAsync(CancellationToken.None); }
        Assert.Multiple(() =>
        {
            Assert.That(host.Writes, Is.EqualTo(2));
            Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
                Is.EqualTo(Convert.ToBase64String(icons.Images[NextKey])));
        });
    }

    [Test]
    public async Task Synchronize_PreviouslyOwnedImage_UpdatesToNewDefaultImageArea()
    {
        var oldArea = new JsonObject { ["x"] = 21, ["y"] = 14, ["width"] = 58, ["height"] = 58 };
        host.Defaults["items"]![0]!["area"] = oldArea.DeepClone();
        host.Current["items"]![0]!["area"] = oldArea.DeepClone();
        using (var first = Create()) { await first.SynchronizeAsync(CancellationToken.None); }

        var newArea = new JsonObject { ["x"] = 17, ["y"] = 8, ["width"] = 66, ["height"] = 58 };
        host.Defaults["items"]![0]!["area"] = newArea.DeepClone();
        SaveBinding(NextKey, "Next shortcut");
        host.Current["items"]![1]!["text"] = "Next shortcut";
        using var updated = Create();
        await updated.SynchronizeAsync(CancellationToken.None);

        Assert.That(JsonNode.DeepEquals(host.Current["items"]![0]!["area"], newArea), Is.True);
    }

    [Test]
    public async Task Synchronize_KeypadBinding_UsesPaddedImageAndFullCanvas()
    {
        var formatterCalls = 0;
        using var synchronizer = Create(keypadImageFormatter: bytes =>
        {
            formatterCalls++;
            Assert.That(bytes, Is.EqualTo(icons.Images[Key]));
            return bytes;
        });
        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(formatterCalls, Is.EqualTo(1));
            Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
                Is.EqualTo(Convert.ToBase64String(icons.Images[Key])));
            Assert.That(JsonNode.DeepEquals(host.Current["items"]![0]!["area"], new JsonObject
            {
                ["x"] = 0,
                ["y"] = 0,
                ["width"] = 100,
                ["height"] = 100
            }), Is.True);
        });
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.EqualTo(1));
    }

    [Test]
    public async Task Synchronize_ActionsRingBinding_DoesNotUseKeypadPadding()
    {
        var ringProfile = Path.Combine(applications, "Loupedeck72", "@_defaultmac",
            "Profiles", "profile", "ProfileInfo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(ringProfile)!);
        File.Copy(profile, ringProfile);
        File.Delete(profile);
        var formatterCalls = 0;
        using var synchronizer = Create(keypadImageFormatter: bytes =>
        {
            formatterCalls++;
            return bytes;
        });
        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(formatterCalls, Is.Zero);
            Assert.That(JsonNode.DeepEquals(host.Current["items"]![0]!["area"],
                host.Defaults["items"]![0]!["area"]), Is.True);
            Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
                Is.EqualTo(Convert.ToBase64String(icons.Images[Key])));
        });
    }

    [Test]
    public async Task Synchronize_ManualEditAfterOwnership_DoesNotOverwriteUserChanges()
    {
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        host.Current["items"]![0]!["imageColor"] = 123;
        SaveBinding(NextKey);
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.EqualTo(1));
    }

    [Test]
    public async Task Synchronize_SelectionChangesDefaultLabel_StillUpdatesOwnedImage()
    {
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        SaveBinding(NextKey, "Next shortcut");
        host.Current["items"]![1]!["text"] = "Next shortcut";
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.EqualTo(2));
    }

    [Test]
    public async Task Synchronize_LegacyOwnershipHash_UpgradesBeforeNextSelection()
    {
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        var records = JsonNode.Parse(File.ReadAllText(ownership))!.AsObject();
        records[records.Single().Key] = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(host.Current.ToJsonString())));
        File.WriteAllText(ownership, records.ToJsonString());

        await synchronizer.SynchronizeAsync(CancellationToken.None);
        SaveBinding(NextKey, "Next shortcut");
        host.Current["items"]![1]!["text"] = "Next shortcut";
        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.That(host.Writes, Is.EqualTo(2));
    }

    [Test]
    public async Task Synchronize_SelectionChangesDuringHostRead_DoesNotApplyStaleCapture()
    {
        host.BeforeGet = count => { if (count == 3) { SaveBinding(NextKey); } };
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.Zero);
    }

    [Test]
    public async Task Synchronize_IconChangesDuringHostRead_DoesNotOverwriteConcurrentCustomization()
    {
        host.BeforeGet = count => { if (count == 3) { host.Current["backgroundColor"] = 123; } };
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Synchronize_MissingSnapshot_UsesDefaultTemplate(bool missingDirectory)
    {
        icons.Images.Clear();
        if (missingDirectory) { icons.MissingException = new DirectoryNotFoundException(); }
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(JsonNode.DeepEquals(host.Current, host.Defaults), Is.True);
    }

    [TestCase("Loupedeck70")]
    [TestCase("Loupedeck72")]
    public async Task Synchronize_MissingSnapshotAfterReload_RestoresExactSelectedShortcutFromCache(string device)
    {
        MoveToDevice(device);
        icons.Images.Clear();
        var identifier = Guid.NewGuid();
        var saved = JsonNode.Parse(File.ReadAllText(profile))!;
        saved["profileActions"]![0]!["actionParameters"]!["parameters"]!["shortcut"] = identifier.ToString("D");
        File.WriteAllText(profile, saved.ToJsonString());
        using (var first = Create(defaultTemplate: host.Defaults))
        {
            await first.SynchronizeAsync(CancellationToken.None);
        }
        byte[] native = [.. Png, 42];
        icons.Images[ShortcutIconCache.CacheKey(identifier)] = native;
        using var cache = new ShortcutIconCache(icons, Path.Combine(directory, "Snapshots"), _ => Assert.Fail());
        using var reloaded = new ShortcutIconTemplates(applications, ownership, cache, host, Svg, Png,
            _ => Assert.Fail(), defaultTemplate: host.Defaults);

        await reloaded.SynchronizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
                Is.EqualTo(Convert.ToBase64String(native)));
            Assert.That(File.Exists(Path.Combine(directory, "Snapshots", Key + ".png")), Is.True);
            Assert.That(host.Writes, Is.EqualTo(1));
        });
    }

    [TestCase("Loupedeck70")]
    [TestCase("Loupedeck72")]
    public async Task Synchronize_CorruptSnapshot_ReportsFailureAndUsesDefaultTemplate(string device)
    {
        MoveToDevice(device);
        icons.Images[Key] = [1, 2, 3];
        var failures = new List<Exception>();
        using var synchronizer = new ShortcutIconTemplates(applications, ownership, icons, host, Svg, Png, failures.Add);
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(failures[0], Is.TypeOf<IOException>());
            Assert.That(JsonNode.DeepEquals(host.Current, host.Defaults), Is.True);
        });
    }

    [TestCase("Loupedeck70", "")]
    [TestCase("Loupedeck70", Key)]
    [TestCase("Loupedeck72", "")]
    [TestCase("Loupedeck72", Key)]
    public async Task Synchronize_Fallback_DoesNotPadOrOverrideFullTemplate(string device, string key)
    {
        MoveToDevice(device);
        SaveBinding(key);
        icons.Images.Clear();
        using var synchronizer = Create(keypadImageFormatter: _ =>
            throw new AssertionException("Fallback artwork must not use native-image formatting."),
            defaultTemplate: host.Defaults);

        await synchronizer.SynchronizeAsync(CancellationToken.None);
        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonNode.DeepEquals(host.Current, host.Defaults), Is.True);
            Assert.That(File.Exists(ownership), Is.True, "Unchanged stock fallbacks must be tracked for template upgrades.");
            Assert.That(host.Writes, Is.Zero);
        });
    }

    [TestCase("Loupedeck70", false)]
    [TestCase("Loupedeck70", true)]
    [TestCase("Loupedeck72", false)]
    [TestCase("Loupedeck72", true)]
    public async Task Synchronize_OwnedFallbackAfterReload_AdoptsEntireReplacementTemplate(
        string device, bool replaceArtwork)
    {
        MoveToDevice(device);
        icons.Images.Clear();
        using (var first = Create(defaultTemplate: host.Defaults))
        {
            await first.SynchronizeAsync(CancellationToken.None);
        }
        var replacement = ReplacementTemplate(replaceArtwork);
        using var reloaded = Create(defaultTemplate: replacement);
        await reloaded.SynchronizeAsync(CancellationToken.None);
        await reloaded.SynchronizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonNode.DeepEquals(host.Current, replacement), Is.True);
            Assert.That(host.Writes, Is.EqualTo(1));
        });
    }

    [TestCase("Loupedeck70")]
    [TestCase("Loupedeck72")]
    public async Task Synchronize_NativeFallbackNative_OnlyFormatsNativeSnapshots(string device)
    {
        MoveToDevice(device);
        var template = (JsonObject)host.Defaults.DeepClone();
        var formattingCalls = 0;
        using var synchronizer = Create(defaultTemplate: template, keypadImageFormatter: bytes =>
        {
            formattingCalls++;
            return bytes;
        });
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        icons.Images.Clear();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(JsonNode.DeepEquals(host.Current, template), Is.True);
        icons.Images[Key] = [.. Png, 1];
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
                Is.EqualTo(Convert.ToBase64String(icons.Images[Key])));
            Assert.That(formattingCalls, Is.EqualTo(device == "Loupedeck70" ? 2 : 0));
            Assert.That(host.Writes, Is.EqualTo(3));
        });
    }

    [TestCase("image")]
    [TestCase("layout")]
    [TestCase("background")]
    [TestCase("text")]
    public async Task Synchronize_CustomizedOwnedFallback_PreservesManualChanges(string customization)
    {
        icons.Images.Clear();
        using (var first = Create(defaultTemplate: host.Defaults))
        {
            await first.SynchronizeAsync(CancellationToken.None);
        }
        if (customization == "image") { host.Current["items"]![0]!["image"] = Convert.ToBase64String([.. Png, 7]); }
        if (customization == "layout") { host.Current["items"]![0]!["area"]!["x"] = 42; }
        if (customization == "background") { host.Current["backgroundColor"] = 123; }
        if (customization == "text") { host.Current["items"]![1]!["text"] = "Custom"; }
        var expected = host.Current.DeepClone();
        using var reloaded = Create(defaultTemplate: ReplacementTemplate(true));
        await reloaded.SynchronizeAsync(CancellationToken.None);

        Assert.That(JsonNode.DeepEquals(host.Current, expected), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Synchronize_ChangedTemplateText_UpdatesOnlyUncustomizedDefaultLabel(bool customized)
    {
        SaveBinding("", "");
        icons.Images.Clear();
        host.Defaults["items"]![1]!["text"] = "Run Shortcut";
        host.Current["items"]![1]!["text"] = "Run Shortcut";
        using (var first = Create(defaultTemplate: host.Defaults))
        {
            await first.SynchronizeAsync(CancellationToken.None);
        }
        if (customized) { host.Current["items"]![1]!["text"] = "Custom"; }
        var replacement = ReplacementTemplate(false);
        replacement["items"]![1]!["text"] = "New default";
        using var reloaded = Create(defaultTemplate: replacement);
        await reloaded.SynchronizeAsync(CancellationToken.None);
        await reloaded.SynchronizeAsync(CancellationToken.None);

        Assert.That(host.Current["items"]![1]!["text"]!.GetValue<string>(),
            Is.EqualTo(customized ? "Custom" : "New default"));
    }

    [TestCase("Loupedeck70")]
    [TestCase("Loupedeck72")]
    public async Task Synchronize_ReplacementFallbackTemplate_DoesNotChangeExistingNativeRendering(string device)
    {
        MoveToDevice(device);
        using (var first = Create(keypadImageFormatter: bytes => bytes))
        {
            await first.SynchronizeAsync(CancellationToken.None);
        }
        var expected = host.Current.DeepClone();
        using var reloaded = Create(defaultTemplate: ReplacementTemplate(true),
            keypadImageFormatter: bytes => bytes);
        await reloaded.SynchronizeAsync(CancellationToken.None);

        Assert.That(JsonNode.DeepEquals(host.Current, expected), Is.True);
    }

    [TestCase("Image")]
    [TestCase("Text")]
    public async Task Synchronize_ReplacementWithOneItem_UsesExactFallbackTemplate(string itemType)
    {
        icons.Images.Clear();
        using (var first = Create(defaultTemplate: host.Defaults))
        {
            await first.SynchronizeAsync(CancellationToken.None);
        }
        var template = ReplacementTemplate(false);
        var items = template["items"]!.AsArray();
        items.Remove(items.OfType<JsonObject>().Single(item => item["itemType"]!.GetValue<string>() != itemType));
        using var reloaded = Create(defaultTemplate: template);
        await reloaded.SynchronizeAsync(CancellationToken.None);
        await reloaded.SynchronizeAsync(CancellationToken.None);

        Assert.That(JsonNode.DeepEquals(host.Current, template), Is.True);
    }

    [Test]
    public async Task Synchronize_NewTemplateArtwork_StillAllowsNativeReplacement()
    {
        host.Defaults["items"]![0]!["image"] = Convert.ToBase64String([.. Png, 8]);
        host.Current["items"]![0]!["image"] = host.Defaults["items"]![0]!["image"]!.DeepClone();
        using var synchronizer = Create(defaultTemplate: host.Defaults);
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
            Is.EqualTo(Convert.ToBase64String(icons.Images[Key])));
    }

    [TestCase("Loupedeck70")]
    [TestCase("Loupedeck72")]
    public async Task Synchronize_PackageDefaultDiffersFromHostDefault_StillAppliesSelectedNativeIcon(string device)
    {
        MoveToDevice(device);
        var template = (JsonObject)host.Defaults.DeepClone();
        host.Defaults["items"]![0]!["image"] = Convert.ToBase64String([.. Png, 42]);
        host.Defaults["items"]![0]!["area"] = new JsonObject
        { ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 100 };
        using var synchronizer = Create(defaultTemplate: template, keypadImageFormatter: bytes => bytes);

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
            Is.EqualTo(Convert.ToBase64String(icons.Images[Key])));
    }

    [TestCase("image")]
    [TestCase("layout")]
    [TestCase("background")]
    [TestCase("text")]
    public async Task Synchronize_DifferingHostDefault_PreservesCustomizedPackageTemplate(string customization)
    {
        var template = (JsonObject)host.Defaults.DeepClone();
        host.Defaults["items"]![0]!["area"] = new JsonObject
        { ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 100 };
        if (customization == "image") { host.Current["items"]![0]!["image"] = Convert.ToBase64String([.. Png, 7]); }
        if (customization == "layout") { host.Current["items"]![0]!["area"]!["x"] = 42; }
        if (customization == "background") { host.Current["backgroundColor"] = 123; }
        if (customization == "text") { host.Current["items"]![1]!["text"] = "Custom"; }
        var expected = host.Current.DeepClone();
        using var synchronizer = Create(defaultTemplate: template);

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonNode.DeepEquals(host.Current, expected), Is.True);
            Assert.That(host.Writes, Is.Zero);
        });
    }

    [TestCase("Loupedeck70")]
    [TestCase("Loupedeck72")]
    public async Task Synchronize_HostAppliedTemplateImageColor_ReplacesStockIconWithNativeIcon(string device)
    {
        MoveToDevice(device);
        var template = (JsonObject)host.Defaults.DeepClone();
        host.Defaults["items"]!.AsArray().RemoveAt(0);
        host.Current["items"]![0]!["imageColor"] = 4278190080L;
        using var synchronizer = Create(defaultTemplate: template, keypadImageFormatter: bytes => bytes);

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
                Is.EqualTo(Convert.ToBase64String(icons.Images[Key])));
            Assert.That(host.Current["items"]![0]!["imageColor"]!.ToJsonString(),
                Is.EqualTo(template["items"]![0]!["imageColor"]!.ToJsonString()));
        });
    }

    [Test]
    public async Task Synchronize_UnownedStockIconWithCustomImageColor_PreservesCustomization()
    {
        var template = (JsonObject)host.Defaults.DeepClone();
        host.Defaults["items"]!.AsArray().RemoveAt(0);
        host.Current["items"]![0]!["imageColor"] = 4289177512L;
        var expected = host.Current.DeepClone();
        using var synchronizer = Create(defaultTemplate: template);

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(JsonNode.DeepEquals(host.Current, expected), Is.True);
            Assert.That(host.Writes, Is.Zero);
        });
    }

    [TestCase("Loupedeck70")]
    [TestCase("Loupedeck72")]
    public async Task Synchronize_HostGeneratedStockFallback_ReplacesNestedBitmapWithCanonicalTemplate(string device)
    {
        MoveToDevice(device);
        icons.Images.Clear();
        var template = (JsonObject)host.Defaults.DeepClone();
        host.Defaults["items"]![0]!["image"] = Convert.ToBase64String([.. Png, 42]);
        host.Defaults["items"]![0]!["area"] = new JsonObject
        { ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 100 };
        host.Current["items"]![0] = host.Defaults["items"]![0]!.DeepClone();
        using var synchronizer = Create(defaultTemplate: template);
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(JsonNode.DeepEquals(host.Current, template), Is.True);
    }

    [Test]
    public async Task Synchronize_RecognizedLegacyStockFallback_AdoptsReplacementTemplate()
    {
        icons.Images.Clear();
        var template = ReplacementTemplate(true);
        using var synchronizer = Create(defaultTemplate: template);
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(JsonNode.DeepEquals(host.Current, template), Is.True);
    }

    [Test]
    public async Task Synchronize_KeypadDecodingFails_ReportsFailureAndUsesFullTemplate()
    {
        var failures = new List<Exception>();
        using var synchronizer = new ShortcutIconTemplates(applications, ownership, icons, host, Svg, Png,
            failures.Add, keypadImageFormatter: _ => throw new IOException("Invalid image payload."),
            defaultTemplate: host.Defaults);
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(JsonNode.DeepEquals(host.Current, host.Defaults), Is.True);
        });
    }

    [Test]
    public async Task Synchronize_FallbackConcurrentEdit_DoesNotOverwriteCustomization()
    {
        icons.Images.Clear();
        using (var first = Create(defaultTemplate: host.Defaults))
        {
            await first.SynchronizeAsync(CancellationToken.None);
        }
        var nextRead = host.Reads + 2;
        host.BeforeGet = count => { if (count == nextRead) { host.Current["backgroundColor"] = 123; } };
        using var synchronizer = Create(defaultTemplate: ReplacementTemplate(true));
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(host.Current["backgroundColor"]!.GetValue<int>(), Is.EqualTo(123));
            Assert.That(host.Writes, Is.Zero);
        });
    }

    [Test]
    public async Task Synchronize_FallbackWriteLosesStyling_ReportsFailureWithoutRefresh()
    {
        using (var first = Create()) { await first.SynchronizeAsync(CancellationToken.None); }
        icons.Images.Clear();
        host.AfterSet = () => host.Current["backgroundColor"] = 123;
        var refreshes = 0;
        using var synchronizer = Create(() => refreshes++, defaultTemplate: host.Defaults);
        Assert.ThrowsAsync<IOException>(() => synchronizer.SynchronizeAsync(CancellationToken.None));
        Assert.That(refreshes, Is.Zero);
    }

    private JsonObject ReplacementTemplate(bool replaceArtwork)
    {
        var template = (JsonObject)host.Defaults.DeepClone();
        template["backgroundColor"] = 4281558681;
        template["items"]![0]!["area"] = new JsonObject
        { ["x"] = 5, ["y"] = 10, ["width"] = 90, ["height"] = 60 };
        template["items"]![1]!["textColor"] = 4278255360;
        template["items"]![1]!["fontSize"] = 9;
        template["items"]![1]!["area"] = new JsonObject
        { ["x"] = 2, ["y"] = 80, ["width"] = 96, ["height"] = 18 };
        if (replaceArtwork) { template["items"]![0]!["image"] = Convert.ToBase64String([.. Png, 9]); }
        return template;
    }

    private void MoveToDevice(string device)
    {
        if (device == "Loupedeck70") { return; }
        var destination = Path.Combine(applications, device, "@_defaultmac", "Profiles", "profile", "ProfileInfo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(profile, destination);
        profile = destination;
    }

    [Test]
    public async Task QueueUpdate_AfterDisposal_DoesNotCallHost()
    {
        var synchronizer = Create();
        synchronizer.QueueUpdate();
        synchronizer.Dispose();
        synchronizer.Dispose();
        synchronizer.QueueUpdate();
        await Task.Delay(400);
        Assert.That(host.Reads, Is.Zero);
    }

    [Test]
    public async Task Synchronize_UnloadAfterHostWrite_PreservesOwnershipForNextSelection()
    {
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        SaveBinding(NextKey);
        using var cancellation = new CancellationTokenSource();
        host.AfterSet = cancellation.Cancel;
        Assert.ThrowsAsync<OperationCanceledException>(() => synchronizer.SynchronizeAsync(cancellation.Token));
        host.AfterSet = null;
        SaveBinding(Key);
        using var reloaded = Create();
        await reloaded.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.EqualTo(3));
    }

    [Test]
    public void Synchronize_OwnershipCannotBeSaved_DoesNotChangeHostIcon()
    {
        File.WriteAllText(Path.Combine(directory, "PluginData"), "Not a directory.");
        using var synchronizer = Create();
        Assert.ThrowsAsync<IOException>(() => synchronizer.SynchronizeAsync(CancellationToken.None));
        Assert.That(host.Writes, Is.Zero);
    }

    [Test]
    public async Task ProfileSaved_AfterEarlierSynchronization_UpdatesWithoutAnotherEditorNotification()
    {
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterSet = () => updated.TrySetResult();
        SaveBinding(NextKey);
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(host.Current["items"]![0]!["image"]!.GetValue<string>(),
            Is.EqualTo(Convert.ToBase64String(icons.Images[NextKey])));
    }

    [Test]
    public async Task Synchronize_RepeatedRequest_DoesNotRewriteUnchangedIcon()
    {
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Writes, Is.EqualTo(1));
    }

    [Test]
    public async Task Synchronize_UnrelatedAction_DoesNotCallHost()
    {
        var saved = JsonNode.Parse(File.ReadAllText(profile))!;
        saved["profileActions"]![0]!["templateActionName"] = "$AnotherPlugin___RunShortcut";
        File.WriteAllText(profile, saved.ToJsonString());
        using var synchronizer = Create();
        await synchronizer.SynchronizeAsync(CancellationToken.None);
        Assert.That(host.Reads, Is.Zero);
    }

    [Test]
    public void Binding_OriginalConstructorAndDeconstruction_RetainSevenMembers()
    {
        var binding = new ShortcutIconBinding("device", "application", "profile", "action", Key, "name", "file");
        var (device, application, profileName, action, key, name, file) = binding;

        Assert.Multiple(() =>
        {
            Assert.That((device, application, profileName, action, key, name, file),
                Is.EqualTo(("device", "application", "profile", "action", Key, "name", "file")));
            Assert.That(typeof(ShortcutIconBinding).GetConstructor(Enumerable.Repeat(typeof(string), 7).ToArray()),
                Is.Not.Null);
            Assert.That(binding.ShortcutId, Is.Null);
        });
    }

    [Test]
    public void Binding_ShortcutId_ParticipatesInEqualityAndWithCopies()
    {
        var identifier = Guid.NewGuid();
        var binding = new ShortcutIconBinding("device", "application", "profile", "action", Key, "name", "file")
        { ShortcutId = identifier };

        Assert.Multiple(() =>
        {
            Assert.That((binding with { }).ShortcutId, Is.EqualTo(identifier));
            Assert.That(binding with { }, Is.EqualTo(binding));
            Assert.That(binding with { ShortcutId = Guid.NewGuid() }, Is.Not.EqualTo(binding));
            Assert.That(binding with { ShortcutId = null }, Is.Not.EqualTo(binding));
        });
    }

    [Test]
    public void ReadBindings_InvalidSnapshotReference_ExcludesBinding()
    {
        SaveBinding("../other-file");
        Assert.That(ShortcutIconTemplates.ReadBindings(applications, profile), Is.Empty);
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("invalid", false)]
    [TestCase("00000000-0000-0000-0000-000000000000", false)]
    [TestCase("11111111-2222-3333-4444-555555555555", true)]
    public void ReadBindings_SelectedIdentifier_OnlyAcceptsValidNonemptyGuid(string? selected, bool valid)
    {
        var saved = JsonNode.Parse(File.ReadAllText(profile))!;
        saved["profileActions"]![0]!["actionParameters"]!["parameters"]!["shortcut"] = selected;
        File.WriteAllText(profile, saved.ToJsonString());

        var binding = ShortcutIconTemplates.ReadBindings(applications, profile).Single();

        Assert.That(binding.ShortcutId, Is.EqualTo(valid ? Guid.Parse(selected!) : (Guid?)null));
    }

    [Test]
    public async Task Synchronize_SelectedIdentifierChangesDuringRead_DoesNotApplyStaleSnapshot()
    {
        var saved = JsonNode.Parse(File.ReadAllText(profile))!;
        var values = saved["profileActions"]![0]!["actionParameters"]!["parameters"]!;
        values["shortcut"] = Guid.NewGuid().ToString("D");
        File.WriteAllText(profile, saved.ToJsonString());
        host.BeforeGet = count =>
        {
            if (count != 3) { return; }
            values["shortcut"] = Guid.NewGuid().ToString("D");
            File.WriteAllText(profile, saved.ToJsonString());
        };
        using var synchronizer = Create();

        await synchronizer.SynchronizeAsync(CancellationToken.None);

        Assert.That(host.Writes, Is.Zero);
    }

    [Test]
    public void Synchronize_Canceled_DoesNotCallHost()
    {
        using var synchronizer = Create();
        Assert.ThrowsAsync<OperationCanceledException>(() => synchronizer.SynchronizeAsync(new CancellationToken(true)));
        Assert.That(host.Reads, Is.Zero);
    }

    private ShortcutIconTemplates Create(Action? refreshConfigurationImages = null,
        Func<byte[], byte[]>? keypadImageFormatter = null, JsonObject? defaultTemplate = null) => new(
        applications, ownership, icons, host, Svg, Png,
        exception => Assert.Fail($"Unexpected background synchronization failure: {exception.GetType().Name}"),
        refreshConfigurationImages, keypadImageFormatter, defaultTemplate);

    private void SaveBinding(string key, string name = "Selected shortcut") => File.WriteAllText(profile, new JsonObject
    {
        ["profileActions"] = new JsonArray(new JsonObject
        {
            ["name"] = ActionName,
            ["templateActionName"] = "$MacShortcutsBridge___RunShortcut",
            ["actionParameters"] = new JsonObject
            {
                ["parameters"] = new JsonObject { ["shortcuticon"] = key, ["shortcutname"] = name }
            }
        })
    }.ToJsonString());

    private sealed class FakeIcons : IShortcutIcons
    {
        public Dictionary<string, byte[]> Images { get; } = [];
        public IOException MissingException { get; set; } = new FileNotFoundException();
        public Task CaptureAsync(Guid identifier, string key) => throw new InvalidOperationException("No capture during synchronization.");
        public byte[] Read(string key) => Images.TryGetValue(key, out var image) ? image : throw MissingException;
    }

    private sealed class FakeHost : IActionIconHost
    {
        public JsonObject Defaults { get; } = new()
        {
            ["backgroundColor"] = 4278190080,
            ["items"] = new JsonArray(new JsonObject
            {
                ["area"] = new JsonObject { ["height"] = 58, ["width"] = 66, ["x"] = 17, ["y"] = 8 },
                ["image"] = Convert.ToBase64String(Svg),
                ["imageColor"] = uint.MaxValue,
                ["imageFileName"] = null,
                ["itemType"] = "Image"
            }, new JsonObject { ["itemType"] = "Text", ["text"] = "Selected shortcut" })
        };
        public JsonObject Current { get; private set; }
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public List<string> Requests { get; } = [];
        public HashSet<string> ReadDevices { get; } = [];
        public Action<int>? BeforeGet { get; set; }
        public Action? AfterSet { get; set; }

        public FakeHost() => Current = (JsonObject)Defaults.DeepClone();

        public Task<JsonObject> GetAsync(ShortcutIconBinding binding, bool defaults, CancellationToken cancellationToken)
        {
            Requests.Add(defaults ? "defaults" : "current");
            Reads++;
            ReadDevices.Add(binding.Device);
            BeforeGet?.Invoke(Reads);
            return Task.FromResult((JsonObject)(defaults ? Defaults : Current).DeepClone());
        }

        public Task SetAsync(ShortcutIconBinding binding, JsonObject icon, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add("set");
            Current = (JsonObject)icon.DeepClone();
            Writes++;
            AfterSet?.Invoke();
            return Task.CompletedTask;
        }
    }
}
