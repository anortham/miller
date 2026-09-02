using Miller.Testing;
using Miller.Testing.Providers.Godot;
using Miller.Testing.Providers.Shared;
using Xunit;

namespace Miller.Tests.Testing.Providers.Godot;

[Collection("GodotEnvironment")]
[Trait("Category", "Scale")]
public sealed class GodotTestProviderScaleTests : IDisposable
{
    private static readonly string[] IsolatedEnvironmentKeys =
    [
        "HOME",
        "XDG_DATA_HOME",
        "XDG_CONFIG_HOME",
        "XDG_CACHE_HOME",
        "TMPDIR",
        "TEMP",
        "TMP",
    ];

    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-godot-scale-").FullName;

    private readonly string _buildRoot =
        Directory.CreateTempSubdirectory("miller-ct-godot-build-").FullName;

    private readonly ITestOutputHelper _output;

    public GodotTestProviderScaleTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        DeleteTree(_root);
        DeleteTree(_buildRoot);
    }

    [Fact]
    public async Task Real_godot_gut_smoke_isolated_and_warm_without_copy_or_import_work()
    {
        string godot = CtProviderTestSupport.RequireGodot();
        string gutRoot = CtProviderTestSupport.RequireGut();
        string? previousGodot = Environment.GetEnvironmentVariable("GODOT");
        string? previousGutRoot = Environment.GetEnvironmentVariable("MILLER_GUT_ROOT");
        Dictionary<string, string?> environmentBefore = CaptureEnvironment();
        try
        {
            Environment.SetEnvironmentVariable("GODOT", godot);
            Environment.SetEnvironmentVariable("MILLER_GUT_ROOT", gutRoot);
            CreateFixture(gutRoot);
            Assert.True(File.Exists(Path.Combine(_root, "assets", "fixture.svg")));
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(_root, "addons", "gut"),
                "*.import",
                SearchOption.AllDirectories));
            string sourceHash = HashTree(_root);
            var workspace = new ContinuousTestWorkspace(
                WorkspaceId: "ws:godot-scale",
                WorkspaceRoot: _root,
                ProjectPath: Path.Combine(_root, "project.godot"),
                BuildOutputRoot: _buildRoot,
                Framework: "gut");
            var provider = new GodotTestProvider(new TestProcessRunner(new TestProcessRunnerOptions
            {
                OutputStallTimeout = TimeSpan.FromMinutes(10),
                MaxCapturedCharactersPerStream = 512 * 1024,
            }));

            ProviderRunResult cold = await provider.RunAsync(
                new ContinuousTestProviderRunRequest(
                    Workspace: workspace,
                    SelectedRevision: "rev-godot-scale-cold",
                    IndexIdentity: "store:godot-scale",
                    RunId: "run:godot-scale-cold",
                    TestCaseIds: ["gut:res://tests/test_primary.gd"]),
                TestContext.Current.CancellationToken);
            ProviderRunResult warm = await provider.RunAsync(
                new ContinuousTestProviderRunRequest(
                    Workspace: workspace,
                    SelectedRevision: "rev-godot-scale-warm",
                    IndexIdentity: "store:godot-scale",
                    RunId: "run:godot-scale-warm",
                    TestCaseIds: [],
                    WholeSuite: true),
                TestContext.Current.CancellationToken);

            ProviderCaseResult measuredCold = Assert.Single(cold.CaseResults);
            ProviderCaseResult measuredWarm = warm.CaseResults.Single(
                result => result.TestCaseId == "gut:res://tests/test_primary.gd");
            _output.WriteLine(
                $"godot metrics: cold_mirror_ms={(double)measuredCold.Metadata["mirror_elapsed_ms"]!:F1} "
                + $"cold_version_ms={(double)measuredCold.Metadata["version_duration_ms"]!:F1} "
                + $"cold_import_ms={(double)measuredCold.Metadata["import_duration_ms"]!:F1} "
                + $"cold_gut_ms={(double)measuredCold.Metadata["gut_duration_ms"]!:F1} "
                + $"cold_report_copy_ms={(double)measuredCold.Metadata["report_copy_duration_ms"]!:F1} "
                + $"cold_candidate_bytes={LongMetric(measuredCold, "project_candidate_bytes")} "
                + $"warm_mirror_ms={(double)measuredWarm.Metadata["mirror_elapsed_ms"]!:F1} "
                + $"warm_version_ms={(double)measuredWarm.Metadata["version_duration_ms"]!:F1} "
                + $"warm_import_ms={(double)measuredWarm.Metadata["import_duration_ms"]!:F1} "
                + $"warm_gut_ms={(double)measuredWarm.Metadata["gut_duration_ms"]!:F1} "
                + $"warm_report_copy_ms={(double)measuredWarm.Metadata["report_copy_duration_ms"]!:F1} "
                + $"warm_candidate_bytes={LongMetric(measuredWarm, "project_candidate_bytes")} "
                + $"godot_home_bytes={LongMetric(measuredWarm, "godot_home_bytes")} "
                + $"warm_entries_updated={LongMetric(measuredWarm, "mirror_entries_updated")} "
                + $"warm_bytes_copied={LongMetric(measuredWarm, "mirror_bytes_copied")} "
                + $"warm_files_hashed={LongMetric(measuredWarm, "mirror_files_hashed")} "
                + $"warm_bytes_hashed={LongMetric(measuredWarm, "mirror_bytes_hashed")}");
            Assert.Equal("passed", cold.Status);
            Assert.Equal("passed", warm.Status);
            Assert.Single(cold.CaseResults);
            Assert.Equal("gut:res://tests/test_primary.gd", cold.CaseResults[0].TestCaseId);
            Assert.Equal(2, warm.CaseResults.Count);
            Assert.Equal(
                ["gut:res://tests/test_dependency.gd", "gut:res://tests/test_primary.gd"],
                warm.CaseResults.Select(result => result.TestCaseId).Order(StringComparer.Ordinal).ToArray());

            ProviderCaseResult coldRow = Assert.Single(cold.CaseResults);
            ProviderCaseResult warmRow = warm.CaseResults.Single(
                result => result.TestCaseId == "gut:res://tests/test_primary.gd");
            Assert.True((bool)coldRow.Metadata["imported"]!);
            Assert.False((bool)warmRow.Metadata["imported"]!);
            Assert.True(LongMetric(coldRow, "mirror_entries_copied") > 0);
            Assert.True(LongMetric(coldRow, "mirror_bytes_copied") > 0);
            Assert.True(LongMetric(coldRow, "mirror_files_hashed") >= 0);
            Assert.Equal(0L, LongMetric(warmRow, "mirror_entries_copied"));
            Assert.Equal(0L, LongMetric(warmRow, "mirror_entries_updated"));
            Assert.Equal(0L, LongMetric(warmRow, "mirror_entries_deleted"));
            Assert.Equal(0L, LongMetric(warmRow, "mirror_bytes_copied"));
            Assert.Equal(0L, LongMetric(warmRow, "mirror_files_hashed"));
            Assert.Equal(0L, LongMetric(warmRow, "mirror_bytes_hashed"));
            Assert.Equal(coldRow.Metadata["source_metadata_digest"], warmRow.Metadata["source_metadata_digest"]);
            AssertMetrics(coldRow);
            AssertMetrics(warmRow);
            Assert.Equal(sourceHash, HashTree(_root));
            AssertEnvironmentUnchanged(environmentBefore);
            Assert.False(Directory.Exists(Path.Combine(_root, ".godot")));
            Assert.False(Directory.Exists(Path.Combine(_root, ".miller-gut-results")));
            Assert.True(Directory.Exists(Path.Combine(
                CtGenerationPaths.CacheRoot(workspace),
                GodotProjectShadow.ProjectCacheName,
                GodotProjectShadow.ProjectMirrorName)));
            Assert.All(
                warm.CaseResults,
                result => AssertContained(_buildRoot, result.Metadata["artifact_path"]!.ToString()!));
            Assert.All(
                Directory.EnumerateFiles(_buildRoot, "*", SearchOption.AllDirectories),
                path => AssertContained(_buildRoot, path));
            _output.WriteLine(
                $"godot metrics: godot={Path.GetFileName(godot)} gut={ReadGutVersion(gutRoot)} "
                + $"cold_imported={(bool)coldRow.Metadata["imported"]!} "
                + $"cold_mirror_ms={(double)coldRow.Metadata["mirror_elapsed_ms"]!:F1} "
                + $"cold_version_ms={(double)coldRow.Metadata["version_duration_ms"]!:F1} "
                + $"cold_import_ms={(double)coldRow.Metadata["import_duration_ms"]!:F1} "
                + $"cold_gut_ms={(double)coldRow.Metadata["gut_duration_ms"]!:F1} "
                + $"cold_report_copy_ms={(double)coldRow.Metadata["report_copy_duration_ms"]!:F1} "
                + $"cold_candidate_bytes={LongMetric(coldRow, "project_candidate_bytes")} "
                + $"warm_imported={(bool)warmRow.Metadata["imported"]!} "
                + $"warm_mirror_ms={(double)warmRow.Metadata["mirror_elapsed_ms"]!:F1} "
                + $"warm_version_ms={(double)warmRow.Metadata["version_duration_ms"]!:F1} "
                + $"warm_import_ms={(double)warmRow.Metadata["import_duration_ms"]!:F1} "
                + $"warm_gut_ms={(double)warmRow.Metadata["gut_duration_ms"]!:F1} "
                + $"warm_report_copy_ms={(double)warmRow.Metadata["report_copy_duration_ms"]!:F1} "
                + $"warm_candidate_bytes={LongMetric(warmRow, "project_candidate_bytes")} "
                + $"godot_home_bytes={LongMetric(warmRow, "godot_home_bytes")}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GODOT", previousGodot);
            Environment.SetEnvironmentVariable("MILLER_GUT_ROOT", previousGutRoot);
        }
    }

    private void CreateFixture(string gutRoot)
    {
        Directory.CreateDirectory(Path.Combine(_root, "tests"));
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "project.godot"), """
            ; Engine configuration file.
            ; Generated for the Miller CT Godot provider Scale smoke.
            config_version=5

            [application]
            config/name="Miller CT Godot fixture"

            [rendering]
            renderer/rendering_method="gl_compatibility"
            renderer/rendering_method.mobile="gl_compatibility"
            """);
        File.WriteAllText(Path.Combine(_root, ".gutconfig.json"), """
            {
              "dirs": ["tests"],
              "tests": [],
              "include_subdirs": true,
              "prefix": "test_",
              "suffix": ".gd",
              "should_exit": true,
              "log_level": 0
            }
            """);
        File.WriteAllText(Path.Combine(_root, "tests", "test_primary.gd"), """
            extends GutTest

            class TestInner extends GutTest:
                func test_inner_class_uses_class_name_dependency():
                    assert_eq(ScaleDependency.answer(), 41)
            """);
        File.WriteAllText(Path.Combine(_root, "tests", "test_dependency.gd"), """
            extends GutTest
            class_name ScaleDependency

            static func answer() -> int:
                return 41

            func test_class_name_dependency():
                assert_eq(answer(), 41)
            """);
        File.WriteAllText(Path.Combine(_root, "assets", "fixture.svg"), """
            <svg xmlns="http://www.w3.org/2000/svg" width="2" height="2">
              <rect width="2" height="2" fill="#ffffff" />
            </svg>
            """);
        CopyDirectory(
            Path.Combine(gutRoot, "addons", "gut"),
            Path.Combine(_root, "addons", "gut"));
    }

    private static Dictionary<string, string?> CaptureEnvironment() =>
        IsolatedEnvironmentKeys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

    private static void AssertEnvironmentUnchanged(IReadOnlyDictionary<string, string?> expected)
    {
        foreach ((string key, string? value) in expected)
            Assert.Equal(value, Environment.GetEnvironmentVariable(key));
    }

    private static void AssertMetrics(ProviderCaseResult result)
    {
        Assert.True((double)result.Metadata["mirror_elapsed_ms"]! >= 0);
        Assert.True((double)result.Metadata["version_duration_ms"]! >= 0);
        Assert.True((double)result.Metadata["import_duration_ms"]! >= 0);
        Assert.True((double)result.Metadata["gut_duration_ms"]! >= 0);
        Assert.True((double)result.Metadata["report_copy_duration_ms"]! >= 0);
        Assert.True(LongMetric(result, "project_candidate_bytes") > 0);
        Assert.True(LongMetric(result, "godot_home_bytes") > 0);
    }

    private static long LongMetric(ProviderCaseResult result, string key) =>
        Convert.ToInt64(result.Metadata[key], System.Globalization.CultureInfo.InvariantCulture);

    private static string ReadGutVersion(string root)
    {
        string path = Path.Combine(root, "addons", "gut", "plugin.cfg");
        return File.ReadAllLines(path)
            .First(line => line.TrimStart().StartsWith("version", StringComparison.Ordinal))
            .Trim();
    }

    private static string HashTree(string root)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path)));
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .Where(file => !file.EndsWith(".import", StringComparison.OrdinalIgnoreCase)))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void AssertContained(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullPath = Path.GetFullPath(path);
        Assert.True(
            string.Equals(fullRoot, fullPath, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"'{path}' was written outside '{root}'.");
    }

    private static void DeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
