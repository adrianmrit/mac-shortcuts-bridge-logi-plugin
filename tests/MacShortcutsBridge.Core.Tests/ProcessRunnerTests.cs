using MacShortcutsBridge.Core;

namespace MacShortcutsBridge.Core.Tests;

[TestFixture]
[Platform("MacOsX")]
public sealed class ProcessRunnerTests
{
    [Test]
    public async Task RunAsync_QuotedArguments_ArePassedLiterally()
    {
        const string input = "\"hello\"; $(not executed) ☕";
        var result = await new ProcessRunner().RunAsync(new ProcessRequest("/usr/bin/printf", ["%s", input], true));
        Assert.That(result.Output, Is.EqualTo(input));
        Assert.That(ProcessRunner.CreateStartInfo(new ProcessRequest("/usr/bin/true", [])).UseShellExecute, Is.False);
    }

    [Test]
    public async Task RunAsync_OutputNotRequested_DiscardsPayload()
    {
        var result = await new ProcessRunner().RunAsync(
            new ProcessRequest("/usr/bin/printf", ["%s", "private shortcut output"]));
        Assert.That(result, Is.EqualTo(new ProcessResult(0, "")));
    }

    [Test]
    public async Task RunAsync_UnsuccessfulProcess_ReturnsExitCode() =>
        Assert.That((await new ProcessRunner().RunAsync(new ProcessRequest("/usr/bin/false", []))).ExitCode, Is.EqualTo(1));

    [Test]
    public void RunAsync_Timeout_StopsBoundedUtility() =>
        Assert.ThrowsAsync<TimeoutException>(() => new ProcessRunner().RunAsync(
            new ProcessRequest("/bin/sleep", ["20"], Timeout: TimeSpan.FromMilliseconds(50))));

    [Test]
    public async Task RunAsync_ClosedStandardInput_ReceivesEof()
    {
        var result = await new ProcessRunner().RunAsync(
            new ProcessRequest("/bin/cat", [], true, TimeSpan.FromSeconds(5)));
        Assert.That(result, Is.EqualTo(new ProcessResult(0, "")));
    }

    [Test]
    public void RunAsync_OutputTooLarge_DrainsThenReportsLimit() =>
        Assert.ThrowsAsync<IOException>(() => new ProcessRunner().RunAsync(new ProcessRequest("/usr/bin/head",
            ["-c", (ProcessRunner.MaximumOutputCharacters + 1).ToString(), "/dev/zero"], true, TimeSpan.FromSeconds(10))));

    [Test]
    public async Task RunAsync_LargeStderr_DrainsWithoutLoggingPayload()
    {
        var result = await new ProcessRunner().RunAsync(new ProcessRequest("/usr/bin/awk",
            ["BEGIN { for (i=0;i<20000;i++) print \"private error payload\" > \"/dev/stderr\"; print \"done\"; exit 3 }"],
            true, TimeSpan.FromSeconds(10)));
        Assert.That(result, Is.EqualTo(new ProcessResult(3, "done\n")));
    }
}
