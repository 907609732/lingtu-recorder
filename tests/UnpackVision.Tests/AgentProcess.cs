using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UnpackVision.Agent.Protocol;

namespace UnpackVision.Tests;

internal static class AgentProcess
{
    public static string ExecutablePath
    {
        get
        {
            var published = Environment.GetEnvironmentVariable("UNPACKVISION_AGENT_EXE");
            return !string.IsNullOrWhiteSpace(published)
                ? Path.GetFullPath(published)
                : Path.Combine(AppContext.BaseDirectory, "agent", "UnpackVision.Agent.exe");
        }
    }

    public static async Task<CliResult> RunAsync(
        string connectionPath,
        params string[] arguments)
    {
        Assert.True(File.Exists(ExecutablePath), $"Agent executable was not copied to {ExecutablePath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["UNPACKVISION_AGENT_CONNECTION"] = connectionPath;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the agent process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Agent CLI process did not exit within 15 seconds.");
        }

        return new CliResult(
            process.ExitCode,
            await stdout,
            await stderr);
    }
}

internal sealed record CliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public JsonDocument ParseJson() => JsonDocument.Parse(StandardOutput);
}
