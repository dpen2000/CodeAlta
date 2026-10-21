namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Where the file-reading tools of a session (<c>read_file</c>, <c>view_image</c>, <c>list_dir</c>, <c>grep</c>) may
/// read: inside the repository the session works in, and nowhere else.
/// </summary>
/// <remarks>
/// A path is checked as the system would follow it: its links are resolved first, so that a link inside an allowed
/// folder does not lead out of it.
/// </remarks>
public sealed class AgentReadGuard
{
    private const int MaximumLinks = 40;

    private readonly string[] _roots;

    /// <summary>
    /// Creates the guard of a session.
    /// </summary>
    /// <param name="readableRoots">The folders the session may read: its repository, worktree and projects.</param>
    /// <exception cref="ArgumentNullException"><paramref name="readableRoots"/> is null.</exception>
    public AgentReadGuard(IEnumerable<string> readableRoots)
    {
        ArgumentNullException.ThrowIfNull(readableRoots);
        _roots = [.. readableRoots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(static root => ResolveLinks(Path.GetFullPath(root)))
            .Distinct(PathComparer)];
    }

    /// <summary>Gets the folders the session may read, with their links resolved.</summary>
    public IReadOnlyList<string> ReadableRoots => _roots;

    /// <summary>
    /// Answers why a path may not be read.
    /// </summary>
    /// <param name="path">The path, absolute or relative to the current directory.</param>
    /// <returns>The reason the agent reads; null when the path is inside a readable folder.</returns>
    public string? Check(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var resolved = ResolveLinks(Path.GetFullPath(path));
        foreach (var root in _roots)
        {
            if (IsInside(resolved, root))
            {
                return null;
            }
        }

        return $"'{path}' is outside the repository this session works in: only files inside the repository can be read.";
    }

    internal static StringComparison PathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsInside(string path, string root)
    {
        if (string.Equals(path, root, PathComparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    // Resolves the links of every part of the path, as the system follows them; what does not exist is kept as is.
    private static string ResolveLinks(string fullPath, int depth = 0)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (parent is null || depth > MaximumLinks)
        {
            return fullPath;
        }

        var candidate = Path.Combine(ResolveLinks(parent, depth + 1), Path.GetFileName(fullPath));
        try
        {
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                return ResolveLinks(target.FullName, depth + 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return candidate;
    }
}
