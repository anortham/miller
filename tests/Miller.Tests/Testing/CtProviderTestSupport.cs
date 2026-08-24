using System.Diagnostics;
using Xunit;

namespace Miller.Tests.Testing;

/// <summary>
/// Shared scaffolding for continuous-testing provider Scale tests. Centralizes toolchain discovery for
/// live provider smokes and skips (never fails) when a prerequisite is absent, so each launch signal
/// lives in exactly one place.
///
/// That single signal is what <see cref="Conventions.CtScaleTraitConventionTests"/> keys on: any test
/// that calls <see cref="RequireDotnet"/>, <see cref="RequireCargo"/>, <see cref="RequireNode"/>, or
/// <see cref="RequirePython"/> spawns a real provider process and MUST therefore carry
/// <c>[Trait("Category","Scale")]</c> so the default fast suite excludes it. Before this helper existed
/// the locator was copy-pasted into the Task 5/8 Scale files, so there was no reliable signal a guard
/// could trust.
/// </summary>
public static class CtProviderTestSupport
{
    public static string? LocateOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string? LocateCMake() =>
        LocateOnPath(OperatingSystem.IsWindows() ? "cmake.exe" : "cmake");

    public static string RequireCMake()
    {
        string? binary = LocateCMake();
        Assert.SkipWhen(binary is null,
            "cmake is required for QtQuickTestProvider Scale smoke");
        return binary!;
    }

    public static string? LocateCTest() =>
        LocateOnPath(OperatingSystem.IsWindows() ? "ctest.exe" : "ctest");

    public static string RequireCTest()
    {
        string? binary = LocateCTest();
        Assert.SkipWhen(binary is null,
            "ctest is required for QtQuickTestProvider Scale smoke");
        return binary!;
    }

    public static string? LocateQtPaths() =>
        LocateOnPath(OperatingSystem.IsWindows() ? "qtpaths6.exe" : "qtpaths6")
        ?? LocateOnPath(OperatingSystem.IsWindows() ? "qtpaths.exe" : "qtpaths");

    public static string RequireQtQuickTestCMakePrefix()
    {
        string? qtPaths = LocateQtPaths();
        Assert.SkipWhen(qtPaths is null,
            "Qt qtpaths is required for QtQuickTestProvider Scale smoke");

        string? prefix = RunQtPathsQuery(qtPaths!);
        string? config = FindQtQuickTestConfig(prefix);
        Assert.SkipWhen(config is null,
            "Qt Quick Test development CMake package is required for QtQuickTestProvider Scale smoke");
        return PrefixFromConfig(config!);
    }

    public static string? LocateDotnet() =>
        LocateOnPath(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

    public static string RequireDotnet()
    {
        string? binary = LocateDotnet();
        Assert.SkipWhen(binary is null,
            "dotnet SDK is required for CT provider Scale smoke");
        return binary!;
    }

    public static string? LocateCargo() =>
        LocateOnPath(OperatingSystem.IsWindows() ? "cargo.exe" : "cargo");

    public static string RequireCargo()
    {
        string? binary = LocateCargo();
        Assert.SkipWhen(binary is null,
            "cargo is required for RustTestProvider Scale smoke");
        return binary!;
    }

    public static string? LocateNode() =>
        LocateOnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");

    public static string RequireNode()
    {
        string? binary = LocateNode();
        Assert.SkipWhen(binary is null,
            "node executable is required for JavaScriptTestProvider Scale smoke");
        return binary!;
    }

    public static string? LocatePython() =>
        LocateOnPath(OperatingSystem.IsWindows() ? "python.exe" : "python3")
        ?? LocateOnPath("python");

    public static string RequirePython()
    {
        string? binary = LocatePython();
        Assert.SkipWhen(binary is null,
            "python is required for PythonTestProvider Scale smoke");
        return binary!;
    }

    public static string? LocatePowerShell() =>
        LocateOnPath("pwsh.exe") ?? LocateOnPath("powershell.exe");

    public static string RequirePowerShell()
    {
        string? binary = LocatePowerShell();
        Assert.SkipWhen(binary is null,
            "PowerShell is required for process-tree Scale tests on Windows");
        return binary!;
    }

    private static string? RunQtPathsQuery(string qtPaths)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = qtPaths,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "--query", "QT_INSTALL_PREFIX" },
            });
            if (process is null)
                return null;
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? FindQtQuickTestConfig(string? qtPrefix)
    {
        var prefixes = new List<string>();
        if (!string.IsNullOrWhiteSpace(qtPrefix))
            prefixes.Add(qtPrefix!);
        string? configuredPrefixes = Environment.GetEnvironmentVariable("CMAKE_PREFIX_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPrefixes))
            prefixes.AddRange(configuredPrefixes.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (string prefix in prefixes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string libraryDirectory in new[] { "lib", "lib64" })
            {
                string candidate = Path.Combine(
                    prefix,
                    libraryDirectory,
                    "cmake",
                    "Qt6QuickTest",
                    "Qt6QuickTestConfig.cmake");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string PrefixFromConfig(string configPath)
    {
        string? cmakeDirectory = Path.GetDirectoryName(configPath);
        string? libraryDirectory = cmakeDirectory is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(cmakeDirectory));
        string? prefix = libraryDirectory is null ? null : Path.GetDirectoryName(libraryDirectory);
        return prefix ?? throw new InvalidOperationException($"Could not derive Qt prefix from '{configPath}'.");
    }
}
