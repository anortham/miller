using Xunit;

namespace Miller.Tests.Testing;

/// <summary>
/// Shared scaffolding for continuous-testing provider Scale tests. Centralizes the ONE thing every
/// live provider smoke needs — locating <c>dotnet</c> / <c>cargo</c> / <c>node</c> / <c>python</c> on
/// <c>PATH</c> and skipping (never failing) when the toolchain is absent — so the launch signal lives
/// in exactly one place.
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
}
