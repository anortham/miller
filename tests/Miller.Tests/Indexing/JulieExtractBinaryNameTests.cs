using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class JulieExtractBinaryNameTests
{
    [Fact]
    public void Locate_ResolvesTheJulieExtractBinary_NotJulieServer()
    {
        string tools = Path.Combine(Path.GetTempPath(), "miller-locate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tools);
        try
        {
            string name = OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract";
            string binary = Path.Combine(tools, name);
            File.WriteAllText(binary, "#!/bin/sh\nexit 0\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var runner = JulieExtractRunner.Locate(tools);

            Assert.Equal(Path.GetFullPath(binary), runner.BinaryPath);
            Assert.EndsWith(name, runner.BinaryPath);
            Assert.DoesNotContain("julie-server", runner.BinaryPath);
        }
        finally { Directory.Delete(tools, recursive: true); }
    }

    [Fact]
    public void Locate_WithNoJulieExtractAnywhere_ThrowsPointingAtTheRenamedRestoreScript()
    {
        string tools = Path.Combine(Path.GetTempPath(), "miller-locate-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tools);
        try
        {
            var ex = Assert.Throws<FileNotFoundException>(
                () => JulieExtractRunner.Locate(tools, pathDirs: Array.Empty<string>()));
            Assert.Contains("restore-julie-extract", ex.Message);
            Assert.DoesNotContain("restore-julie-server", ex.Message);
        }
        finally { Directory.Delete(tools, recursive: true); }
    }
}
