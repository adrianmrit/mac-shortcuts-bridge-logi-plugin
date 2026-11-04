using MacShortcutsBridge.Core;

namespace MacShortcutsBridge.Core.Tests;

[TestFixture]
public sealed class ShortcutIconsTests
{
    private string directory = null!;
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [SetUp]
    public void SetUp() => directory = Directory.CreateTempSubdirectory("ShortcutsIconTests-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(directory, true);

    [TestCase("../elsewhere")]
    [TestCase("")]
    [TestCase("some-shortcut-name")]
    public void Read_InvalidReference_RejectsPaths(string key) =>
        Assert.Throws<ArgumentException>(() => new ShortcutIcons(new IconProcesses(), directory).Read(key));

    [Test]
    public async Task CaptureAsync_ValidIcon_WritesSnapshotAndCleansTemporaryFiles()
    {
        var process = new IconProcesses();
        var icons = new ShortcutIcons(process, directory);
        var id = Guid.NewGuid();
        await icons.CaptureAsync(id, Key);
        Assert.Multiple(() =>
        {
            Assert.That(icons.Read(Key), Is.EqualTo(IconProcesses.Image));
            Assert.That(process.Requests.Select(request => request.FileName),
                Is.EqualTo(new[] { "/usr/bin/osascript", "/usr/bin/sips" }));
            Assert.That(process.Requests[0].Arguments[^2], Is.EqualTo(id.ToString("D")));
            Assert.That(process.Requests.All(request => request.Timeout.HasValue), Is.True);
            Assert.That(Directory.Exists(process.TemporaryDirectory), Is.False);
        });
    }

    [Test]
    public void CaptureAsync_PermissionDenied_CleansTemporaryFilesAndReportsFailure()
    {
        var process = new IconProcesses { ExitCode = 1 };
        Assert.ThrowsAsync<IOException>(() => new ShortcutIcons(process, directory).CaptureAsync(Guid.NewGuid(), Key));
        Assert.That(Directory.Exists(process.TemporaryDirectory), Is.False);
        Assert.That(Directory.GetFiles(directory), Is.Empty);
    }

    [Test]
    public void Read_OversizedIcon_ReportsFailureWithoutReadingPayload()
    {
        using (var stream = File.Create(Path.Combine(directory, Key + ".png")))
        {
            stream.SetLength(ShortcutIcons.MaximumIconBytes + 1);
        }
        Assert.Throws<IOException>(() => new ShortcutIcons(new IconProcesses(), directory).Read(Key));
    }

    [TestCase(new byte[] { })]
    [TestCase(new byte[] { 1, 2, 3 })]
    public void ValidatePng_InvalidHeader_ReportsFailure(byte[] data) =>
        Assert.Throws<IOException>(() => ShortcutIcons.ValidatePng(data));

    [TestCase(false)]
    [TestCase(true)]
    public void ReadSnapshot_WithoutCache_PreservesMissingException(bool missingDirectory)
    {
        var path = missingDirectory ? Path.Combine(directory, "Missing") : directory;
        var icons = new ShortcutIcons(new IconProcesses(), path);

        Assert.Throws(missingDirectory ? typeof(DirectoryNotFoundException) : typeof(FileNotFoundException),
            () => ShortcutIcons.ReadSnapshot(icons, Key, Guid.NewGuid()));
    }

    private sealed class IconProcesses : IProcessRunner
    {
        public static readonly byte[] Image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jEioAAAAASUVORK5CYII=");
        public int ExitCode { get; init; }
        public List<ProcessRequest> Requests { get; } = [];
        public string? TemporaryDirectory { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request)
        {
            Requests.Add(request);
            TemporaryDirectory = Path.GetDirectoryName(request.Arguments[^1]);
            if (ExitCode == 0) { File.WriteAllBytes(request.Arguments[^1], Image); }
            return Task.FromResult(new ProcessResult(ExitCode, ""));
        }
    }
}
