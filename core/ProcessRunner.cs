using System.Diagnostics;
using System.Text;

namespace MacShortcutsBridge.Core;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool CaptureOutput = false,
    TimeSpan? Timeout = null);

public sealed record ProcessResult(int ExitCode, string Output);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request);
}

public sealed class ProcessRunner : IProcessRunner
{
    public const int MaximumOutputCharacters = 4 * 1024 * 1024;

    public async Task<ProcessResult> RunAsync(ProcessRequest request)
    {
        using var process = new Process { StartInfo = CreateStartInfo(request) };
        if (!process.Start())
        {
            throw new IOException("Could not start the requested macOS utility.");
        }

        process.StandardInput.Close();
        var output = DrainAsync(process.StandardOutput, request.CaptureOutput);
        var error = DrainAsync(process.StandardError, false);
        using var timeout = request.Timeout is { } duration
            ? new CancellationTokenSource(duration)
            : new CancellationTokenSource();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process can exit between cancellation and Kill.
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw new TimeoutException("The macOS utility did not respond in time.");
        }

        var streams = await Task.WhenAll(output, error).ConfigureAwait(false);
        if (streams[0].Overflow)
        {
            throw new IOException("The shortcut catalog exceeded the supported output size.");
        }
        return new ProcessResult(process.ExitCode, streams[0].Text);
    }

    public static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    private static async Task<(string Text, bool Overflow)> DrainAsync(StreamReader stream, bool capture)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var overflow = false;
        int count;
        while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            if (!capture)
            {
                continue;
            }
            var remaining = MaximumOutputCharacters - text.Length;
            text.Append(buffer, 0, Math.Min(count, remaining));
            overflow |= count > remaining;
        }
        return (text.ToString(), overflow);
    }
}
