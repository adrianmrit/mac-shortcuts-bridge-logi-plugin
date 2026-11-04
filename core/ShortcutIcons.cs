using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace MacShortcutsBridge.Core;

public interface IShortcutIcons
{
    Task CaptureAsync(Guid identifier, string key);
    byte[] Read(string key);
}

public sealed class ShortcutIcons(IProcessRunner processes, string directory) : IShortcutIcons
{
    public const int MaximumIconBytes = 1024 * 1024;

    public async Task CaptureAsync(Guid identifier, string key)
    {
        if (identifier == Guid.Empty)
        {
            throw new ArgumentException("A shortcut identifier is required.", nameof(identifier));
        }
        ValidateKey(key);
        Directory.CreateDirectory(directory);
        var temporary = Directory.CreateTempSubdirectory("MacShortcutsBridge-");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        try
        {
            var tiff = Path.Combine(temporary.FullName, "icon.tiff");
            var png = Path.Combine(temporary.FullName, "icon.png");
            using var resource = typeof(ShortcutIcons).Assembly.GetManifestResourceStream(
                "MacShortcutsBridge.Core.ShortcutIcon.applescript")
                ?? throw new InvalidOperationException("The icon extraction script is missing.");
            using var script = new StreamReader(resource);
            var extracted = await processes.RunAsync(new ProcessRequest("/usr/bin/osascript",
                ["-e", await script.ReadToEndAsync().ConfigureAwait(false), "--", identifier.ToString("D"), tiff],
                Timeout: TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            if (extracted.ExitCode != 0)
            {
                throw new IOException("Shortcut icon unavailable. macOS Automation permission may be required.");
            }
            var converted = await processes.RunAsync(new ProcessRequest("/usr/bin/sips",
                ["-s", "format", "png", tiff, "--out", png], Timeout: TimeSpan.FromSeconds(10)))
                .ConfigureAwait(false);
            if (converted.ExitCode != 0)
            {
                throw new IOException("The shortcut icon could not be converted.");
            }
            _ = ReadBounded(png);
            // Each configured selection owns a snapshot, including during async capture.
            File.Move(png, Path.Combine(directory, key + ".png"));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    public byte[] Read(string key)
    {
        ValidateKey(key);
        return ReadBounded(Path.Combine(directory, key + ".png"));
    }

    public static byte[] ReadSnapshot(IShortcutIcons source, string key, Guid? identifier)
    {
        try { return source.Read(key); }
        catch (FileNotFoundException)
        {
            if (!TryRestoreSnapshot()) { throw; }
        }
        catch (DirectoryNotFoundException)
        {
            if (!TryRestoreSnapshot()) { throw; }
        }
        return source.Read(key);

        bool TryRestoreSnapshot() =>
            identifier is { } id && id != Guid.Empty && source is ICachedShortcutIcons cache
                && cache.TryCopyCached(id, key);
    }

    public static void ValidatePng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(signature)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != 13
            || !bytes[12..16].SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]) is 0 or > 4096
            || BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]) is 0 or > 4096)
        {
            throw new IOException("The shortcut icon is not a supported PNG image.");
        }
    }

    internal static void ValidateKey(string key)
    {
        if (!Regex.IsMatch(key, @"\A[0-9A-F]{64}\z", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Invalid shortcut icon reference.", nameof(key));
        }
    }

    internal static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > MaximumIconBytes)
        {
            throw new IOException("The shortcut icon has an unsupported size.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        ValidatePng(bytes);
        return bytes;
    }
}
