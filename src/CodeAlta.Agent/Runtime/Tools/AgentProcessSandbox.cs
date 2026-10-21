using System.Diagnostics;
using System.Text;

namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Wraps the processes an agent starts (<c>shell_command</c>) in an operating-system sandbox that lets them read and
/// write only inside the repository the session works in (and the system folders needed to run programs).
/// </summary>
/// <remarks>
/// Linux uses bubblewrap (<c>bwrap</c>), macOS uses <c>sandbox-exec</c>. Where neither is available (Windows, or a
/// machine without the tool) <see cref="TryWrap"/> returns false: the caller decides whether to run unsandboxed.
/// </remarks>
public sealed class AgentProcessSandbox
{
    private static readonly string[] LinuxSystemFolders = ["/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/etc", "/opt", "/run/systemd/resolve"];

    private readonly string[] _roots;
    private readonly bool _requireSandbox;

    /// <summary>Creates the sandbox of a session.</summary>
    /// <param name="roots">The folders the sandboxed processes may read and write.</param>
    /// <param name="requireSandbox">True to refuse to run a command where no sandbox is available.</param>
    /// <exception cref="ArgumentNullException"><paramref name="roots"/> is null.</exception>
    public AgentProcessSandbox(IEnumerable<string> roots, bool requireSandbox = false)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = [.. roots.Where(static r => !string.IsNullOrWhiteSpace(r)).Select(static r => Path.GetFullPath(r)).Distinct()];
        _requireSandbox = requireSandbox;
    }

    /// <summary>Gets the folders the sandboxed processes may use.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Gets whether a command is refused where no sandbox is available.</summary>
    public bool RequireSandbox => _requireSandbox;

    /// <summary>
    /// Rewrites a start info so that the process runs inside the sandbox.
    /// </summary>
    /// <param name="startInfo">The start info, rewritten in place.</param>
    /// <returns>True when the process is sandboxed; false when no sandbox is available on this system.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="startInfo"/> is null.</exception>
    public bool TryWrap(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (OperatingSystem.IsLinux() && FindOnPath("bwrap") is { } bwrap)
        {
            Wrap(startInfo, bwrap, CreateBubblewrapArguments(startInfo));
            return true;
        }

        if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/sandbox-exec"))
        {
            Wrap(startInfo, "/usr/bin/sandbox-exec", CreateSandboxExecArguments(startInfo));
            return true;
        }

        return false;
    }

    private IReadOnlyList<string> CreateBubblewrapArguments(ProcessStartInfo startInfo)
    {
        var args = new List<string> { "--die-with-parent", "--unshare-pid", "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp" };
        foreach (var folder in LinuxSystemFolders)
        {
            if (Directory.Exists(folder))
            {
                args.Add("--ro-bind");
                args.Add(folder);
                args.Add(folder);
            }
        }

        foreach (var root in _roots)
        {
            args.Add("--bind");
            args.Add(root);
            args.Add(root);
        }

        args.Add("--chdir");
        args.Add(startInfo.WorkingDirectory);
        return args;
    }

    private IReadOnlyList<string> CreateSandboxExecArguments(ProcessStartInfo startInfo)
    {
        var profile = new StringBuilder("(version 1)(allow default)");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            profile.Append("(deny file-read* (subpath ").Append(Quote(home)).Append("))");
        }

        profile.Append("(deny file-write*)(allow file-write* (subpath \"/dev\") (subpath \"/private/tmp\") (subpath \"/private/var/folders\")");
        foreach (var root in _roots)
        {
            profile.Append(" (subpath ").Append(Quote(root)).Append(')');
        }

        profile.Append(')');
        foreach (var root in _roots)
        {
            profile.Append("(allow file-read* (subpath ").Append(Quote(root)).Append("))");
        }

        return ["-p", profile.ToString()];
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void Wrap(ProcessStartInfo startInfo, string wrapper, IReadOnlyList<string> wrapperArguments)
    {
        var original = new List<string> { startInfo.FileName };
        original.AddRange(startInfo.ArgumentList);
        startInfo.FileName = wrapper;
        startInfo.ArgumentList.Clear();
        foreach (var argument in wrapperArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (Path.GetFileName(wrapper) == "bwrap")
        {
            startInfo.ArgumentList.Add("--");
        }

        foreach (var argument in original)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
