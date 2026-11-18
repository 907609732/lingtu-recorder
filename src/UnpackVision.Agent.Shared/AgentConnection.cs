using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UnpackVision.Agent.Protocol;

/// <summary>
/// The connection record is deliberately loopback-only. A tampered file cannot
/// redirect the client to a LAN/Internet host or a hostname that would be
/// re-resolved by DNS.
/// </summary>
public sealed record AgentConnectionInfo
{
    public int Version { get; init; } = AgentApiContract.Version;
    public string BaseUrl { get; init; } = "http://127.0.0.1:1/";
    public string Token { get; init; } = string.Empty;
    public int Pid { get; init; }
    public DateTimeOffset StartedAt { get; init; }

    public override string ToString() => $"UnpackVision agent at {BaseUrl} (pid {Pid})";
}

public static class AgentConnectionFile
{
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnpackVision",
        "ai",
        "connection.json");

    public static string GetLockPath(string path) => Path.GetFullPath(path) + ".lock";

    public static string ResolvePath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var configured = Environment.GetEnvironmentVariable(
            AgentApiContract.ConnectionPathEnvironmentVariable);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DefaultPath : configured);
    }

    public static async Task<AgentConnectionInfo> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                throw new AgentUnavailableException(
                    "UnpackVision desktop is not running or has not finished initialization.");
            }
            if (file.Length is <= 0 or > AgentApiContract.MaxConnectionFileBytes)
            {
                throw new AgentUnavailableException(
                    "The UnpackVision agent connection file is invalid. Restart UnpackVision.");
            }

            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var connection = await JsonSerializer.DeserializeAsync<AgentConnectionInfo>(
                stream,
                AgentJson.Options,
                cancellationToken);
            if (connection is null || !TryValidate(connection, out var normalized, out _))
            {
                throw new AgentUnavailableException(
                    "The UnpackVision agent connection file is invalid. Restart UnpackVision.");
            }

            return normalized!;
        }
        catch (AgentUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AgentUnavailableException(
                "UnpackVision desktop is not running or has not finished initialization.");
        }
    }

    public static async Task WriteAtomicAsync(
        string path,
        AgentConnectionInfo connection,
        CancellationToken cancellationToken = default,
        TimeSpan? lockTimeout = null)
    {
        if (!TryValidate(connection, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(connection));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The connection file needs a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        await using var fileLock = await AcquireFileLockAsync(
            fullPath,
            lockTimeout ?? DefaultLockTimeout,
            cancellationToken);
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, AgentJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// A desktop instance may only remove the file it published. Token and pid
    /// both have to match, which prevents an older instance from deleting the
    /// connection of a newer instance during overlapping shutdown.
    /// </summary>
    public static async Task<bool> TryDeleteIfOwnedAsync(
        string path,
        string token,
        int pid,
        CancellationToken cancellationToken = default,
        TimeSpan? lockTimeout = null)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            await using var fileLock = await AcquireFileLockAsync(
                fullPath,
                lockTimeout ?? DefaultLockTimeout,
                cancellationToken);
            var connection = await ReadAsync(fullPath, cancellationToken);
            if (connection.Pid != pid || !TokensEqual(connection.Token, token))
            {
                return false;
            }

            File.Delete(fullPath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public static bool TryValidate(
        AgentConnectionInfo connection,
        out AgentConnectionInfo? normalized,
        out string? error)
    {
        normalized = null;
        if (connection.Version != AgentApiContract.Version)
        {
            error = "Unsupported agent connection version.";
            return false;
        }
        if (connection.Pid <= 0)
        {
            error = "The agent connection pid is invalid.";
            return false;
        }
        if (!TryNormalizeLoopbackBaseUrl(connection.BaseUrl, out var baseUrl, out error))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(connection.Token) ||
            connection.Token.Length is < 16 or > 256 ||
            connection.Token.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            error = "The agent connection token is invalid.";
            return false;
        }

        normalized = connection with { BaseUrl = baseUrl! };
        return true;
    }

    public static bool TryNormalizeLoopbackBaseUrl(
        string? value,
        out string? normalized,
        out string? error)
    {
        normalized = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) ||
            uri.HostNameType != UriHostNameType.IPv4 ||
            uri.Port is < 1 or > 65535 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/"))
        {
            error = "The agent connection must be a literal http://127.0.0.1:port/ address.";
            return false;
        }

        normalized = $"http://127.0.0.1:{uri.Port}/";
        error = null;
        return true;
    }

    private static async Task<FileStream> AcquireFileLockAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var lockPath = GetLockPath(path);
        if (timeout <= TimeSpan.Zero)
        {
            timeout = TimeSpan.FromMilliseconds(1);
        }
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, cancellationToken);
            }
            catch (IOException exception)
            {
                throw new TimeoutException(
                    "Timed out waiting for the agent connection file lock.",
                    exception);
            }
        }
    }

    private static bool TokensEqual(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }
}
