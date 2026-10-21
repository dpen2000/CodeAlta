using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using Microsoft.Mxc.Sdk.V1.Policy;
using XenoAtom.Logging;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Runs the processes an agent starts (<c>shell_command</c>) inside an MXC container
/// (<see href="https://github.com/microsoft/mxc"/>): they may write only inside the repository the session works in
/// (and the temporary folder), and read only it plus the tools and system folders needed to run programs.
/// </summary>
/// <remarks>
/// MXC picks the native backend of the host: ProcessContainer on Windows, Bubblewrap on Linux, Seatbelt on macOS.
/// </remarks>
public sealed class AgentProcessSandbox
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Agent.Sandbox");

    private readonly string[] _roots;

    /// <summary>Creates the sandbox of a session.</summary>
    /// <param name="roots">The folders the sandboxed processes may read and write.</param>
    /// <param name="requireSandbox">True (the default) to refuse to run a command where MXC cannot contain it; false to run it unwrapped instead.</param>
    /// <exception cref="ArgumentNullException"><paramref name="roots"/> is null.</exception>
    public AgentProcessSandbox(IEnumerable<string> roots, bool requireSandbox = true)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = [.. roots.Where(static r => !string.IsNullOrWhiteSpace(r)).Select(static r => Path.GetFullPath(r)).Distinct()];
        RequireSandbox = requireSandbox;
    }

    /// <summary>Gets the folders the sandboxed processes may use.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Gets whether a command is refused where MXC cannot contain it.</summary>
    public bool RequireSandbox { get; }

    /// <summary>
    /// Answers whether MXC can contain processes on this host.
    /// </summary>
    /// <returns>True when the MXC SDK and a backend are available.</returns>
    public static bool IsAvailable()
    {
        try
        {
            return MxcPlatform.GetPlatformSupport().IsSupported;
        }
        catch (Exception ex) when (ex is MxcException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the MXC request that runs a process as <paramref name="startInfo"/> describes it.
    /// </summary>
    /// <param name="startInfo">The process: its program, arguments, folder and environment.</param>
    /// <param name="timeoutMs">The time limit of the command in milliseconds; null or 0 for none.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="startInfo"/> is null.</exception>
    public ContainerRequest CreateRequest(ProcessStartInfo startInfo, int? timeoutMs = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var readWrite = new List<string>(_roots);
        var readOnly = new List<string>();
        AddFragment(Filesystem.GetAvailableToolsPolicy(), readOnly, readWrite);
        AddFragment(Filesystem.GetTemporaryFilesPolicy(), readOnly, readWrite);
        if (OperatingSystem.IsWindows())
        {
            AddFragment(Filesystem.GetUserProfilePolicy(), readOnly, readWrite);
        }
        else
        {
            foreach (var folder in new[] { "/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/etc", "/opt", "/System", "/Library", "/private/etc" })
            {
                if (Directory.Exists(folder))
                {
                    readOnly.Add(folder);
                }
            }
        }

        var environment = new Dictionary<string, string>();
        foreach (var pair in startInfo.Environment)
        {
            if (pair.Value is not null)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        var request = new ContainerRequest(BuildCommandLine(startInfo))
        {
            Filesystem = new FilesystemPolicy
            {
                ReadwritePaths = [.. readWrite.Distinct()],
                ReadonlyPaths = [.. readOnly.Except(readWrite).Distinct()],
            },
            WorkingDirectory = startInfo.WorkingDirectory,
            Environment = environment,
        };
        if (timeoutMs is > 0)
        {
            request.TimeoutMs = (uint)timeoutMs.Value;
        }

        return request;
    }

    /// <summary>
    /// Starts a process inside the container.
    /// </summary>
    /// <param name="startInfo">The process.</param>
    /// <param name="timeoutMs">The time limit of the command in milliseconds; null or 0 for none.</param>
    /// <returns>The live process, or null when MXC cannot contain processes on this host.</returns>
    /// <exception cref="MxcException">MXC refused the request.</exception>
    public MxcProcess? TrySpawn(ProcessStartInfo startInfo, int? timeoutMs = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        PlatformSupport? support = null;
        string? failure = null;
        try
        {
            support = MxcPlatform.GetPlatformSupport();
        }
        catch (Exception ex) when (ex is MxcException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            failure = ex.Message;
        }

        if (support is not { IsSupported: true })
        {
            Log(() => $"MXC sandbox unavailable: {failure ?? support?.Reason ?? "unsupported platform"}; requireSandbox={RequireSandbox} workingDirectory='{startInfo.WorkingDirectory}'");
            return null;
        }

        var request = CreateRequest(startInfo, timeoutMs);
        // The environment and the command line may hold secrets: they are not logged, only the program.
        Log(() => $"MXC sandbox spawn: backends=[{string.Join(", ", support.AvailableMethods)}] program='{startInfo.FileName}' workingDirectory='{request.WorkingDirectory}' timeoutMs={request.TimeoutMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} "
            + $"readwrite=[{string.Join(", ", request.Filesystem?.ReadwritePaths ?? [])}] readonly=[{string.Join(", ", request.Filesystem?.ReadonlyPaths ?? [])}] denied=[{string.Join(", ", request.Filesystem?.DeniedPaths ?? [])}]");
        try
        {
            var process = MxcContainer.Spawn(request);
            if (process.Warnings.Count > 0)
            {
                Log(() => $"MXC sandbox warnings: {string.Join("; ", process.Warnings)}");
            }

            return process;
        }
        catch (MxcException ex)
        {
            Log(() => $"MXC sandbox spawn failed: code={ex.Code} native={ex.NativeCode} {ex.Message}");
            throw;
        }
    }

    private static void Log(Func<string> message)
    {
        if (LogManager.IsInitialized)
        {
            Logger.Info(message());
        }
    }

    private static void AddFragment(FilesystemPolicyResult fragment, List<string> readOnly, List<string> readWrite)
    {
        readOnly.AddRange(fragment.ReadonlyPaths);
        readWrite.AddRange(fragment.ReadwritePaths);
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var parts = new List<string> { startInfo.FileName };
        parts.AddRange(startInfo.ArgumentList);
        return string.Join(' ', parts.Select(static p => OperatingSystem.IsWindows() ? QuoteWindows(p) : QuotePosix(p)));
    }

    private static string QuotePosix(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // The command line is parsed by CreateProcess: backslashes before a quote are doubled, and the quote escaped.
    private static string QuoteWindows(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '\n', '"']) < 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
