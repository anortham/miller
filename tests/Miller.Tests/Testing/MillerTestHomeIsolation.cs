using System.Runtime.CompilerServices;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Testing;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MillerHomeEnvironmentCollection
{
    public const string Name = nameof(MillerHomeEnvironmentCollection);
}

internal static class MillerTestHomeIsolation
{
    private const int MaxOwnedHomeEntries = 100_000;

    internal const string OwnedHomePrefix = "miller-tests-home-";

    internal static string? OriginalMillerHome { get; private set; }

    internal static string? OriginalHome { get; private set; }

    internal static string? OriginalUserProfile { get; private set; }

    internal static string? OwnedMillerHome { get; private set; }

    [ModuleInitializer]
    internal static void Initialize()
    {
        OriginalMillerHome = Environment.GetEnvironmentVariable(MillerHome.EnvironmentVariable);
        OriginalHome = Environment.GetEnvironmentVariable("HOME");
        OriginalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");

        if (!string.IsNullOrWhiteSpace(OriginalMillerHome))
            return;

        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string home = Path.Combine(
            tempRoot,
            $"{OwnedHomePrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        OwnedMillerHome = home;
        Environment.SetEnvironmentVariable(MillerHome.EnvironmentVariable, home);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    internal static bool IsOwnedTempHomePath(string? candidate)
        => IsOwnedTempHomePath(candidate, OwnedMillerHome);

    internal static bool IsOwnedTempHomePath(string? candidate, string? expectedOwnedHome)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(expectedOwnedHome))
            return false;

        try
        {
            string candidateFull = Path.GetFullPath(candidate);
            string expectedFull = Path.GetFullPath(expectedOwnedHome);
            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (!Path.IsPathFullyQualified(candidateFull)
                || !Path.IsPathFullyQualified(expectedFull)
                || !Path.IsPathFullyQualified(tempRoot)
                || !PathsEqual(candidateFull, expectedFull))
                return false;

            if (!IsDirectChildOf(candidateFull, tempRoot)
                || !IsDirectChildOf(expectedFull, tempRoot))
                return false;

            string name = Path.GetFileName(candidateFull);
            if (!name.StartsWith(OwnedHomePrefix, StringComparison.Ordinal))
                return false;

            string suffix = name[OwnedHomePrefix.Length..];
            int separator = suffix.IndexOf('-');
            return separator > 0
                && int.TryParse(suffix[..separator], out int processId)
                && processId > 0
                && Guid.TryParseExact(suffix[(separator + 1)..], "N", out _);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsSafeOwnedHomeTree(string? candidate)
        => IsSafeOwnedHomeTree(
            candidate,
            OwnedMillerHome,
            File.GetAttributes,
            path => Directory.EnumerateFileSystemEntries(path));

    internal static bool IsSafeOwnedHomeTree(
        string? candidate,
        string? expectedOwnedHome,
        Func<string, FileAttributes> getAttributes,
        Func<string, IEnumerable<string>> enumerateChildren)
    {
        if (!IsOwnedTempHomePath(candidate, expectedOwnedHome)
            || candidate is null
            || getAttributes is null
            || enumerateChildren is null)
            return false;

        string root;
        try
        {
            root = Path.GetFullPath(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        var pending = new Stack<string>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        pending.Push(root);
        seen.Add(root);

        while (pending.Count > 0)
        {
            string path = pending.Pop();
            FileAttributes attributes;
            try
            {
                attributes = getAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || (path == root && (attributes & FileAttributes.Directory) == 0))
                    return false;

                if ((attributes & FileAttributes.Directory) == 0)
                    continue;

                IEnumerable<string>? children = enumerateChildren(path);
                if (children is null)
                    return false;

                foreach (string child in children)
                {
                    if (string.IsNullOrWhiteSpace(child) || !Path.IsPathFullyQualified(child))
                        return false;

                    string childFull = Path.GetFullPath(child);
                    if (!IsDescendantOf(root, childFull)
                        || !seen.Add(childFull)
                        || seen.Count > MaxOwnedHomeEntries)
                        return false;

                    pending.Push(childFull);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDirectChildOf(string path, string parent)
    {
        string? directory = Path.GetDirectoryName(path);
        return directory is not null && PathsEqual(directory, parent);
    }

    private static bool IsDescendantOf(string root, string candidate)
    {
        if (!Path.IsPathFullyQualified(candidate) || PathsEqual(root, candidate))
            return false;

        string relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void OnProcessExit(object? sender, EventArgs e) => Cleanup();

    private static void Cleanup()
    {
        try
        {
            Environment.SetEnvironmentVariable(MillerHome.EnvironmentVariable, OriginalMillerHome);
        }
        catch
        {
        }

        try
        {
            string? home = OwnedMillerHome;
            if (!IsOwnedTempHomePath(home)
                || home is null
                || !Directory.Exists(home)
                || (File.GetAttributes(home) & FileAttributes.ReparsePoint) != 0)
                return;

            if (!IsSafeOwnedHomeTree(home))
                return;

            Directory.Delete(home, recursive: true);
        }
        catch
        {
        }
    }
}
