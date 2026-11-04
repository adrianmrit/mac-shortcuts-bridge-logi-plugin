using System.Net.WebSockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MacShortcutsBridge.Core;

public sealed record ShortcutIconBinding(string Device, string Application, string Profile,
    string Action, string IconKey, string ShortcutName, string ProfileFile)
{
    public Guid? ShortcutId { get; init; }
}

public interface IActionIconHost
{
    Task<JsonObject> GetAsync(ShortcutIconBinding binding, bool defaults, CancellationToken cancellationToken);
    Task SetAsync(ShortcutIconBinding binding, JsonObject icon, CancellationToken cancellationToken);
}

public sealed class ActionIconHost(string portFile) : IActionIconHost
{
    private static int nextRequestId;

    public static Uri ReadEndpoint(string portFile)
    {
        using var stream = File.OpenRead(portFile);
        if (stream.Length is 0 or > 16) { throw new IOException("The service port file is invalid."); }
        using var reader = new StreamReader(stream);
        if (!int.TryParse(reader.ReadToEnd().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535) { throw new IOException("The service port is invalid."); }
        return new UriBuilder("ws", "127.0.0.1", port, "/configui3").Uri;
    }

    public async Task<JsonObject> GetAsync(ShortcutIconBinding binding, bool defaults, CancellationToken cancellationToken)
    {
        var data = await RequestAsync(defaults ? "GetDefaultActionIcon" : "GetActionIcon",
            binding, null, cancellationToken).ConfigureAwait(false);
        return data?["actionIcon"]?.DeepClone() as JsonObject
            ?? throw new IOException("The service did not return an action icon.");
    }

    public async Task SetAsync(ShortcutIconBinding binding, JsonObject icon, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("SetActionIcon", binding, icon, cancellationToken).ConfigureAwait(false);
        if (result is not JsonValue value || !value.TryGetValue<bool>(out var saved) || !saved)
        {
            throw new IOException("The service did not save the action icon.");
        }
    }

    private async Task<JsonNode?> RequestAsync(string name, ShortcutIconBinding binding,
        JsonObject? data, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(ReadEndpoint(portFile), timeout.Token).ConfigureAwait(false);
        var requestId = Interlocked.Increment(ref nextRequestId);
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = requestId,
            name,
            parameters = new
            {
                deviceType = binding.Device,
                applicationName = binding.Application,
                profileName = binding.Profile,
                actionName = binding.Action
            },
            data
        });
        await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        var buffer = new byte[8192];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (received.MessageType != WebSocketMessageType.Text)
                {
                    throw new IOException("The icon service closed the connection or returned an invalid message.");
                }
                message.Write(buffer, 0, received.Count);
                if (message.Length > 4 * 1024 * 1024) { throw new IOException("The icon response is too large."); }
            } while (!received.EndOfMessage);

            var response = JsonNode.Parse(message.ToArray()) as JsonObject
                ?? throw new IOException("The icon service returned an invalid response.");
            if (response["id"]?.GetValue<int>() != requestId) { continue; }
            if (response["name"]?.GetValue<string>() != name || response["failed"]?.GetValue<bool>() != false)
            {
                throw new IOException("The icon service rejected the request.");
            }
            return response["data"]?.DeepClone();
        }
    }
}

public sealed class ShortcutIconTemplates : IDisposable
{
    private const string KeypadDeviceId = "Loupedeck70";
    private const long HostTemplateImageColor = 0xFF000000;
    private const string RingDeviceId = "Loupedeck72";
    private readonly string applicationsDirectory;
    private readonly string ownershipFile;
    private readonly IShortcutIcons icons;
    private readonly IActionIconHost host;
    private readonly Func<byte[], byte[]>? keypadImageFormatter;
    private readonly byte[][] legacyDefaultImages;
    private readonly JsonObject? defaultTemplate;
    private readonly Action<Exception> reportFailure;
    private readonly Action? refreshConfigurationImages;
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private readonly FileSystemWatcher? profiles;
    private bool pending;
    private bool disposed;
    private Task? worker;

    public ShortcutIconTemplates(string applicationsDirectory, string ownershipFile, IShortcutIcons icons,
        IActionIconHost host, byte[] defaultSvg, byte[] defaultPng, Action<Exception> reportFailure,
        Action? refreshConfigurationImages = null, Func<byte[], byte[]>? keypadImageFormatter = null,
        JsonObject? defaultTemplate = null)
    {
        this.applicationsDirectory = applicationsDirectory;
        this.ownershipFile = ownershipFile;
        this.icons = icons;
        this.host = host;
        this.defaultTemplate = (JsonObject?)defaultTemplate?.DeepClone();
        this.keypadImageFormatter = keypadImageFormatter;
        legacyDefaultImages = [defaultSvg, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(defaultSvg)
            .Replace("style=\"fill:", "fill=\"", StringComparison.Ordinal)), defaultPng];
        this.reportFailure = reportFailure;
        this.refreshConfigurationImages = refreshConfigurationImages;
        if (Directory.Exists(applicationsDirectory))
        {
            profiles = new FileSystemWatcher(applicationsDirectory, "ProfileInfo.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            profiles.Changed += (_, _) => QueueUpdate();
            profiles.Created += (_, _) => QueueUpdate();
            profiles.Renamed += (_, _) => QueueUpdate();
            profiles.Error += (_, args) => reportFailure(args.GetException());
            profiles.EnableRaisingEvents = true;
        }
    }

    public void QueueUpdate()
    {
        lock (gate)
        {
            if (disposed) { return; }
            pending = true;
            if (worker is null || worker.IsCompleted) { worker = Task.Run(ProcessUpdatesAsync); }
        }
    }

    private async Task ProcessUpdatesAsync()
    {
        while (true)
        {
            lock (gate)
            {
                if (!pending || stopping.IsCancellationRequested) { worker = null; return; }
                pending = false;
            }
            try
            {
                // Editor-finished notifications can precede the profile file's save.
                await Task.Delay(300, stopping.Token).ConfigureAwait(false);
                await SynchronizeAsync(stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or WebSocketException or OperationCanceledException or ArgumentException
                or InvalidOperationException)
            {
                reportFailure(exception);
            }
        }
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(applicationsDirectory)) { return; }
        var ownership = File.Exists(ownershipFile)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(ReadBounded(ownershipFile))
                ?? throw new IOException("The icon ownership record is invalid.")
            : new Dictionary<string, string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var hasBindings = false;
        foreach (var device in new[] { KeypadDeviceId, RingDeviceId })
        {
            var directory = Path.Combine(applicationsDirectory, device);
            if (!Directory.Exists(directory)) { continue; }
            foreach (var file in Directory.EnumerateFiles(directory, "ProfileInfo.json", options))
            {
                foreach (var binding in ReadBindings(applicationsDirectory, file))
                {
                    hasBindings = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    await UpdateAsync(binding, ownership, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (hasBindings) { refreshConfigurationImages?.Invoke(); }
    }

    private async Task UpdateAsync(ShortcutIconBinding binding, Dictionary<string, string> ownership,
        CancellationToken cancellationToken)
    {
        var image = LoadSnapshot(binding);
        var useKeypadImageFormatting = image is not null
            && binding.Device == KeypadDeviceId && keypadImageFormatter is not null;
        var current = await host.GetAsync(binding, false, cancellationToken).ConfigureAwait(false);
        var imageItem = FindImage(current);
        JsonObject? defaults = null;
        var legacy = false;
        if (imageItem is null)
        {
            defaults = await ReadDefaultsAsync(binding, image is null, cancellationToken).ConfigureAwait(false);
            legacy = IsLegacyTextDefault(current, defaults);
        }
        var currentImage = imageItem?["image"]?.GetValue<string>() ?? "";
        var identity = Hash(Encoding.UTF8.GetBytes(
            $"{binding.Device}\0{binding.Application}\0{binding.Profile}\0{binding.Action}"));
        var fingerprint = Fingerprint(current);
        var isOwned = ownership.TryGetValue(identity, out var previous)
            && (previous.Split(':').Contains(fingerprint, StringComparer.Ordinal)
                || previous == Hash(Encoding.UTF8.GetBytes(Canonical(current).ToJsonString())));
        if (!CanModifyLabel(binding, current, isOwned ? previous : null)) { return; }
        if (isOwned)
        {
            defaults ??= await ReadDefaultsAsync(binding, image is null, cancellationToken).ConfigureAwait(false);
        }
        if (image is not null && currentImage == Convert.ToBase64String(image))
        {
            var currentArea = FindImage(current)?["area"];
            var expectedArea = useKeypadImageFormatting
                ? new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 100 }
                : defaults is null ? null : FindImage(defaults)?["area"];
            if (!isOwned || JsonNode.DeepEquals(currentArea, expectedArea))
            {
                if (isOwned && previous != OwnershipFingerprint(current))
                {
                    await SaveConfirmedOwnershipAsync(ownership, identity, current, cancellationToken).ConfigureAwait(false);
                }
                return;
            }
        }
        JsonNode? hostAppliedTemplateColor = null;
        if (!isOwned && !legacy)
        {
            defaults ??= await ReadDefaultsAsync(binding, image is null, cancellationToken).ConfigureAwait(false);
            var hostDefaults = image is not null || defaultTemplate is null ? defaults
                : await host.GetAsync(binding, true, cancellationToken).ConfigureAwait(false);
            var packageDefaults = image is null || defaultTemplate is null ? defaults
                : await ReadDefaultsAsync(binding, true, cancellationToken).ConfigureAwait(false);
            if (!TryRecognizeDefault(current, hostDefaults, packageDefaults, out hostAppliedTemplateColor)) { return; }
        }

        defaults ??= await ReadDefaultsAsync(binding, image is null, cancellationToken).ConfigureAwait(false);
        var updatedSource = image is null || legacy || imageItem is null ? defaults : current;
        var updated = BuildUpdatedIcon(updatedSource, defaults, image, useKeypadImageFormatting,
            isOwned, hostAppliedTemplateColor);
        if (JsonNode.DeepEquals(current, updated))
        {
            if (!isOwned || previous != OwnershipFingerprint(current))
            {
                await SaveConfirmedOwnershipAsync(ownership, identity, current, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // Do not apply an old capture to a binding that was reselected while awaiting the service.
        if (!ReadBindings(applicationsDirectory, binding.ProfileFile).Contains(binding)) { return; }
        var latest = await host.GetAsync(binding, false, cancellationToken).ConfigureAwait(false);
        if (!JsonNode.DeepEquals(current, latest)
            || !ReadBindings(applicationsDirectory, binding.ProfileFile).Contains(binding)) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        // Retain both states if unload or a failed response interrupts the host write.
        ownership[identity] = $"{OwnershipFingerprint(current)}:{OwnershipFingerprint(updated)}";
        await SaveOwnershipAsync(ownership, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await host.SetAsync(binding, updated, cancellationToken).ConfigureAwait(false);
        var saved = await host.GetAsync(binding, false, cancellationToken).ConfigureAwait(false);
        if (image is null ? !JsonNode.DeepEquals(saved, updated)
            : FindImage(saved)?["image"]?.GetValue<string>() != Convert.ToBase64String(image))
        {
            throw new IOException("The service did not retain the shortcut image.");
        }
        await SaveConfirmedOwnershipAsync(ownership, identity, saved, cancellationToken).ConfigureAwait(false);
    }

    private byte[]? LoadSnapshot(ShortcutIconBinding binding)
    {
        if (string.IsNullOrEmpty(binding.IconKey)) { return null; }
        try
        {
            var image = ShortcutIcons.ReadSnapshot(icons, binding.IconKey, binding.ShortcutId);
            ShortcutIcons.ValidatePng(image);
            if (binding.Device == KeypadDeviceId && keypadImageFormatter is not null)
            {
                image = keypadImageFormatter(image);
                ShortcutIcons.ValidatePng(image);
            }
            return image;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            reportFailure(exception);
            return null;
        }
    }

    private bool CanModifyLabel(ShortcutIconBinding binding, JsonObject current, string? previousOwnership)
    {
        var text = (current["items"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(item => item["itemType"]?.GetValue<string>() == "Text");
        var defaultLabel = (defaultTemplate?["items"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(item => item["itemType"]?.GetValue<string>() == "Text")?["text"]?.GetValue<string>()
            ?? "Run Shortcut";
        var currentLabel = text?["text"]?.GetValue<string>();
        // Text is excluded from image ownership so reselection works; track default labels separately.
        var labelIsOwned = previousOwnership is not null && string.IsNullOrEmpty(binding.ShortcutName)
            && (previousOwnership.Split(':').Contains(LabelFingerprint(current), StringComparer.Ordinal)
                || (!previousOwnership.Contains("label-", StringComparison.Ordinal) && currentLabel == "Run Shortcut"));
        return text is null || currentLabel == (string.IsNullOrEmpty(binding.ShortcutName)
            ? defaultLabel : binding.ShortcutName) || labelIsOwned;
    }

    private bool TryRecognizeDefault(JsonObject current, JsonObject hostDefaults, JsonObject packageDefaults,
        out JsonNode? hostAppliedTemplateColor)
    {
        var imageItem = FindImage(current);
        var currentImage = imageItem?["image"]?.GetValue<string>() ?? "";
        var templateImage = FindImage(packageDefaults)?["image"]?.GetValue<string>();
        var legacyImage = legacyDefaultImages.Any(bytes => currentImage == Convert.ToBase64String(bytes));
        var matchesTemplate = (currentImage == templateImage || legacyImage)
            && JsonNode.DeepEquals(WithoutImage(current), WithoutImage(packageDefaults));
        var matchesLegacyHostTemplate = legacyImage
            && JsonNode.DeepEquals(WithoutImage(current), WithoutImage(hostDefaults));
        // Options+ stores an opaque black image color when it applies the package icon template.
        hostAppliedTemplateColor = !matchesTemplate && currentImage == templateImage
            && imageItem?["imageColor"]?.ToJsonString() == HostTemplateImageColor.ToString(CultureInfo.InvariantCulture)
            && FindImage(packageDefaults)?["imageColor"] is { } templateColor
            && JsonNode.DeepEquals(WithoutImage(current), WithoutImage(WithImageColor(packageDefaults,
                HostTemplateImageColor)))
                ? templateColor.DeepClone() : null;
        return matchesTemplate || matchesLegacyHostTemplate || hostAppliedTemplateColor is not null
            || JsonNode.DeepEquals(current, hostDefaults);
    }

    private static JsonObject BuildUpdatedIcon(JsonObject source, JsonObject defaults, byte[]? image,
        bool useKeypadImageFormatting, bool isOwned, JsonNode? hostAppliedTemplateColor)
    {
        var updated = (JsonObject)source.DeepClone();
        if (image is null) { return updated; }
        var imageItem = FindImage(updated) ?? throw new IOException("The default icon has no image item.");
        if (hostAppliedTemplateColor is not null) { imageItem["imageColor"] = hostAppliedTemplateColor; }
        if (useKeypadImageFormatting)
        {
            imageItem["area"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 100 };
        }
        else if (isOwned && FindImage(defaults)?["area"] is { } defaultArea)
        {
            imageItem["area"] = defaultArea.DeepClone();
        }
        imageItem["image"] = Convert.ToBase64String(image);
        imageItem["imageFileName"] = null;
        return updated;
    }

    private Task SaveConfirmedOwnershipAsync(Dictionary<string, string> ownership, string identity, JsonObject icon,
        CancellationToken cancellationToken)
    {
        ownership[identity] = OwnershipFingerprint(icon);
        return SaveOwnershipAsync(ownership, cancellationToken);
    }

    private async Task<JsonObject> ReadDefaultsAsync(ShortcutIconBinding binding, bool fallback,
        CancellationToken cancellationToken)
    {
        var defaults = !fallback || defaultTemplate is null
            ? await host.GetAsync(binding, true, cancellationToken).ConfigureAwait(false)
            : (JsonObject)defaultTemplate.DeepClone();
        if (!string.IsNullOrEmpty(binding.ShortcutName)
            && (defaults["items"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(item => item["itemType"]?.GetValue<string>() == "Text") is { } text)
        {
            text["text"] = binding.ShortcutName;
        }
        return defaults;
    }

    private async Task SaveOwnershipAsync(Dictionary<string, string> ownership, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(ownershipFile)!);
        var temporary = ownershipFile + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(ownership), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, ownershipFile, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    public static IReadOnlyList<ShortcutIconBinding> ReadBindings(string applicationsDirectory, string file)
    {
        var relative = Path.GetRelativePath(applicationsDirectory, file).Split(Path.DirectorySeparatorChar);
        if (relative.Length != 5 || relative[0] is not (KeypadDeviceId or RingDeviceId)
            || relative[2] != "Profiles" || relative[4] != "ProfileInfo.json") { return []; }
        var profile = JsonNode.Parse(ReadBounded(file)) as JsonObject
            ?? throw new IOException("The saved profile is invalid.");
        if (profile["profileActions"] is not JsonArray actions) { return []; }
        var bindings = new List<ShortcutIconBinding>();
        foreach (var action in actions.OfType<JsonObject>())
        {
            if (action["templateActionName"]?.GetValue<string>() != "$MacShortcutsBridge___RunShortcut") { continue; }
            var name = action["name"]?.GetValue<string>();
            if (name is null || !name.StartsWith("$@Generic___@ProfileAction___", StringComparison.Ordinal)) { continue; }
            var values = action["actionParameters"]?["parameters"] as JsonObject;
            var key = values?["shortcuticon"]?.GetValue<string>() ?? "";
            if (key.Length != 0 && (key.Length != 64 || key.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))) { continue; }
            Guid? identifier = Guid.TryParse(values?["shortcut"]?.GetValue<string>(), out var id)
                && id != Guid.Empty ? id : null;
            bindings.Add(new(relative[0], relative[1], relative[3], name, key,
                values?["shortcutname"]?.GetValue<string>() ?? "", file)
            { ShortcutId = identifier });
        }
        return bindings;
    }

    private static JsonObject? FindImage(JsonObject icon) =>
        (icon["items"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(item => item["itemType"]?.GetValue<string>() == "Image");

    private static JsonObject WithoutImage(JsonObject icon)
    {
        var copy = (JsonObject)icon.DeepClone();
        if (FindImage(copy) is { } image) { image["image"] = ""; }
        return copy;
    }

    private static JsonObject WithImageColor(JsonObject icon, long color)
    {
        var copy = (JsonObject)icon.DeepClone();
        if (FindImage(copy) is { } image) { image["imageColor"] = color; }
        return copy;
    }

    private static bool IsLegacyTextDefault(JsonObject current, JsonObject defaults)
    {
        var expected = (JsonObject)defaults.DeepClone();
        if (expected["items"] is not JsonArray items || items.Count != 2 || FindImage(expected) is not { } image)
        {
            return false;
        }
        items.Remove(image);
        if (items[0] is not JsonObject text || text["itemType"]?.GetValue<string>() != "Text") { return false; }
        text["area"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 100 };
        return JsonNode.DeepEquals(current, expected);
    }

    private static string Fingerprint(JsonObject icon)
    {
        var copy = (JsonObject)icon.DeepClone();
        foreach (var text in (copy["items"] as JsonArray)?.OfType<JsonObject>()
            .Where(item => item["itemType"]?.GetValue<string>() == "Text") ?? [])
        {
            // Selection updates the default label independently of the image.
            text["text"] = "";
            text.Remove("originalText");
        }
        return Hash(Encoding.UTF8.GetBytes(Canonical(copy).ToJsonString()));
    }

    private static string OwnershipFingerprint(JsonObject icon) => $"{Fingerprint(icon)}:{LabelFingerprint(icon)}";

    private static string LabelFingerprint(JsonObject icon) => "label-" + Hash(Encoding.UTF8.GetBytes(
        (icon["items"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(item => item["itemType"]?.GetValue<string>() == "Text")?["text"]?.GetValue<string>() ?? ""));

    private static JsonNode Canonical(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value is null ? null : Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(item => item is null ? null : Canonical(item)).ToArray()),
        _ => node.DeepClone()
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] ReadBounded(string file)
    {
        using var stream = File.OpenRead(file);
        if (stream.Length > 4 * 1024 * 1024) { throw new IOException("The icon profile or ownership file is too large."); }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public void Dispose()
    {
        Task? active;
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            stopping.Cancel();
            pending = false;
            active = worker;
        }
        profiles?.Dispose();
        if (active is null || active.IsCompleted) { stopping.Dispose(); }
        else { _ = active.ContinueWith(_ => stopping.Dispose(), TaskScheduler.Default); }
    }
}
