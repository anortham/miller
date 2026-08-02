using Miller.Indexing;

namespace Miller.Tests.Support;

/// <summary>
/// A stand-in <c>julie-extract</c> for fast tests that need the extractor to FAIL without the real binary: it
/// answers <c>--version</c> with a parseable version (so the leadership-eligibility gate lets the caller through)
/// and exits non-zero for every other invocation.
/// </summary>
internal static class StubJulieExtract
{
    /// <summary>Whether this platform can host the stub (it is a POSIX shell script).</summary>
    internal static bool Supported => !OperatingSystem.IsWindows();

    /// <summary>Write the stub into <paramref name="toolsRoot"/> where <c>JulieExtractRunner.Locate</c> finds it.</summary>
    internal static string WriteFailing(string toolsRoot, string version = "9.9.9")
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The julie-extract stub is a POSIX shell script.");

        Directory.CreateDirectory(toolsRoot);
        string binary = Path.Combine(toolsRoot, "julie-extract");
        File.WriteAllText(
            binary,
            $"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then
              echo "julie-extract {version}"
              exit 0
            fi
            echo "stub julie-extract: refusing to extract" 1>&2
            exit 1

            """);
        File.SetUnixFileMode(
            binary,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return binary;
    }

    /// <summary>The runner over a freshly written failing stub.</summary>
    internal static JulieExtractRunner FailingRunner(string toolsRoot)
    {
        WriteFailing(toolsRoot);
        return JulieExtractRunner.Locate(toolsRoot);
    }
}
