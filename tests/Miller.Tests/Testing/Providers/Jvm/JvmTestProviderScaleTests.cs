using Miller.Testing;
using Miller.Testing.Providers.Jvm;
using Miller.Testing.Providers.Shared;
using Xunit;

namespace Miller.Tests.Testing.Providers.Jvm;

[Trait("Category", "Scale")]
public sealed class JvmTestProviderScaleTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-jvm-scale-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Gradle_smoke_discovers_and_runs_two_junit5_methods_without_source_build_output()
    {
        string java = CtProviderTestSupport.RequireJava();
        string gradle = CtProviderTestSupport.RequireGradle();
        Assert.True(File.Exists(java));
        Assert.True(File.Exists(gradle));
        File.WriteAllText(Path.Combine(_root, "build.gradle"), """
            plugins {
                id 'java'
            }
            buildDir = file("${rootDir}/build")
            test {
                reports.junitXml.outputLocation = file("${rootDir}/build/test-results/test")
            }
            repositories {
                mavenCentral()
            }
            dependencies {
                testImplementation 'org.junit.jupiter:junit-jupiter:5.10.2'
            }
            test {
                useJUnitPlatform()
            }
            """);
        string testPath = Path.Combine(_root, "src", "test", "java", "sample", "CalculatorTest.java");
        Directory.CreateDirectory(Path.GetDirectoryName(testPath)!);
        File.WriteAllText(testPath, """
            package sample;

            import static org.junit.jupiter.api.Assertions.assertTrue;
            import org.junit.jupiter.api.Test;

            class CalculatorTest {
                @Test void adds() { assertTrue(true); }
                @Test void subtracts() { assertTrue(true); }
            }
            """);
        string sourceHash = HashTree(_root);
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:jvm-scale",
            WorkspaceRoot: _root,
            ProjectPath: Path.Combine(_root, "build.gradle"),
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-jvm-scale"),
            Framework: "gradle");
        var provider = new JvmTestProvider(new TestProcessRunner());

        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, discovered.Count);
        Assert.All(discovered, test => Assert.Equal("jvm", test.Metadata["language_family"]));

        ProviderRunResult result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-jvm-scale",
                IndexIdentity: "store:jvm-scale",
                TestCaseIds: discovered.Select(test => test.Id).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal("passed", result.Status);
        Assert.Equal(2, result.CaseResults.Count);
        Assert.Equal(sourceHash, HashTree(_root));
        Assert.False(Directory.Exists(Path.Combine(_root, "build")));
        Assert.All(result.CaseResults, row => Assert.StartsWith(workspace.BuildOutputRoot, row.Metadata["artifact_path"]!.ToString()!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Maven_smoke_discovers_and_runs_two_junit_classes_without_target_output()
    {
        _ = CtProviderTestSupport.RequireJava();
        _ = CtProviderTestSupport.RequireMaven();
        File.WriteAllText(Path.Combine(_root, "pom.xml"), """
            <project xmlns="http://maven.apache.org/POM/4.0.0"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     xsi:schemaLocation="http://maven.apache.org/POM/4.0.0 https://maven.apache.org/xsd/maven-4.0.0.xsd">
              <modelVersion>4.0.0</modelVersion>
              <groupId>sample</groupId>
              <artifactId>miller-ct-maven</artifactId>
              <version>1.0-SNAPSHOT</version>
              <properties>
                <maven.compiler.release>17</maven.compiler.release>
                <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
              </properties>
              <dependencies>
                <dependency>
                  <groupId>org.junit.jupiter</groupId>
                  <artifactId>junit-jupiter</artifactId>
                  <version>5.10.2</version>
                  <scope>test</scope>
                </dependency>
              </dependencies>
              <build>
                <plugins>
                  <plugin>
                    <groupId>org.apache.maven.plugins</groupId>
                    <artifactId>maven-surefire-plugin</artifactId>
                    <version>3.2.5</version>
                  </plugin>
                </plugins>
              </build>
            </project>
            """);
        string first = Path.Combine(_root, "src", "test", "java", "sample", "FirstTest.java");
        string second = Path.Combine(_root, "src", "test", "java", "sample", "SecondTests.java");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllText(first, """
            package sample;

            import static org.junit.jupiter.api.Assertions.assertTrue;
            import org.junit.jupiter.api.Test;

            class FirstTest {
                @Test void passes() { assertTrue(true); }
            }
            """);
        File.WriteAllText(second, """
            package sample;

            import static org.junit.jupiter.api.Assertions.assertTrue;
            import org.junit.jupiter.api.Test;

            class SecondTests {
                @Test void passes() { assertTrue(true); }
            }
            """);
        string sourceHash = HashTree(_root, "ct-jvm-maven-scale");
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:jvm-maven-scale",
            WorkspaceRoot: _root,
            ProjectPath: Path.Combine(_root, "pom.xml"),
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-jvm-maven-scale"),
            Framework: "maven");
        var provider = new JvmTestProvider(new TestProcessRunner());

        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            ["sample.FirstTest", "sample.SecondTests"],
            discovered.Select(test => test.Selector).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        Assert.All(discovered, test => Assert.Equal("jvm", test.Metadata["language_family"]));
        Assert.All(discovered, test => Assert.Equal(test.Selector, test.Metadata["selector"]));

        ProviderRunResult result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-jvm-maven-scale",
                IndexIdentity: "store:jvm-maven-scale",
                TestCaseIds: discovered.Select(test => test.Id).ToArray()),
            TestContext.Current.CancellationToken);

        Assert.Equal("passed", result.Status);
        Assert.Equal(2, result.CaseResults.Count);
        Assert.Equal(sourceHash, HashTree(_root, "ct-jvm-maven-scale"));
        Assert.Empty(Directory.EnumerateDirectories(_root, "target", SearchOption.AllDirectories));
        Assert.All(result.CaseResults, row => Assert.StartsWith(workspace.BuildOutputRoot, row.Metadata["artifact_path"]!.ToString()!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sbt_smoke_keeps_the_source_immutable_and_reuses_the_warm_shadow()
    {
        string java = CtProviderTestSupport.RequireJava();
        string sbt = CtProviderTestSupport.RequireSbt();
        Assert.True(File.Exists(java));
        Assert.True(File.Exists(sbt));
        File.WriteAllText(Path.Combine(_root, "build.sbt"), """
            ThisBuild / scalaVersion := "2.13.14"
            libraryDependencies += "org.scalatest" %% "scalatest" % "3.2.18" % Test
            """);
        string first = Path.Combine(_root, "src", "test", "scala", "sample", "FirstTest.scala");
        string second = Path.Combine(_root, "src", "test", "scala", "sample", "SecondTest.scala");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        File.WriteAllText(first, """
            package sample
            import org.scalatest.funsuite.AnyFunSuite
            class FirstTest extends AnyFunSuite {
              test("adds") { assert(1 + 1 == 2) }
            }
            """);
        File.WriteAllText(second, """
            package sample
            import org.scalatest.funsuite.AnyFunSuite
            class SecondTest extends AnyFunSuite {
              test("subtracts") { assert(2 - 1 == 1) }
            }
            """);
        string sourceHash = HashTree(_root, "ct-jvm-sbt-scale");
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:jvm-sbt-scale",
            WorkspaceRoot: _root,
            ProjectPath: Path.Combine(_root, "build.sbt"),
            BuildOutputRoot: Path.Combine(_root, ".miller", "ct-jvm-sbt-scale"),
            Framework: "sbt");
        var backend = new SbtTestBackend(new TestProcessRunner());
        var provider = new JvmTestProvider(backend);

        DateTimeOffset coldSyncStarted = DateTimeOffset.UtcNow;
        SbtWorkspaceShadowResult coldSync = SbtWorkspaceShadow.Sync(
            workspace,
            TestContext.Current.CancellationToken);
        TimeSpan coldSyncElapsed = DateTimeOffset.UtcNow - coldSyncStarted;
        string versionWorkingDirectory = Path.GetDirectoryName(coldSync.ShadowProjectPath)
            ?? coldSync.ShadowRoot;
        string javaVersion = await ProbeVersionAsync(
            java,
            ["-version"],
            versionWorkingDirectory,
            TestContext.Current.CancellationToken);
        string sbtVersion = await ProbeVersionAsync(
            sbt,
            ["--version"],
            versionWorkingDirectory,
            TestContext.Current.CancellationToken);
        IReadOnlyList<ProviderTestCase> discovered = await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);

        DateTimeOffset warmSyncStarted = DateTimeOffset.UtcNow;
        SbtWorkspaceShadowResult warmSync = SbtWorkspaceShadow.Sync(
            workspace,
            TestContext.Current.CancellationToken);
        TimeSpan warmSyncElapsed = DateTimeOffset.UtcNow - warmSyncStarted;
        IReadOnlyList<ProviderTestCase> warmDiscovered = await provider.DiscoverAsync(
            workspace,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, discovered.Count);
        Assert.Equal(discovered.Select(test => test.Id), warmDiscovered.Select(test => test.Id));
        Assert.Equal(0, warmSync.EntriesCopied);
        Assert.Equal(0, warmSync.BytesCopied);

        DateTimeOffset runStarted = DateTimeOffset.UtcNow;
        ProviderRunResult result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "rev-jvm-sbt-scale",
                IndexIdentity: "store:jvm-sbt-scale",
                TestCaseIds: discovered.Select(test => test.Id).ToArray()),
            TestContext.Current.CancellationToken);
        TimeSpan runElapsed = DateTimeOffset.UtcNow - runStarted;

        Assert.Equal("passed", result.Status);
        Assert.Equal(2, result.CaseResults.Count);
        Assert.Equal(sourceHash, HashTree(_root, "ct-jvm-sbt-scale"));
        Assert.False(Directory.Exists(Path.Combine(_root, "target")));
        Assert.False(Directory.Exists(Path.Combine(_root, "project", "target")));
        HashSet<string> sourceFiles =
        [
            "build.sbt",
            Path.Combine("src", "test", "scala", "sample", "FirstTest.scala"),
            Path.Combine("src", "test", "scala", "sample", "SecondTest.scala"),
        ];
        Assert.All(
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Where(path => !path.StartsWith(Path.Combine(_root, ".miller"), StringComparison.Ordinal)),
            path => Assert.Contains(Path.GetRelativePath(_root, path), sourceFiles));
        Console.WriteLine(
            $"sbt metrics: java_version={javaVersion} sbt_version={sbtVersion} "
            + $"cold_sync_ms={coldSyncElapsed.TotalMilliseconds:F1} "
            + $"warm_sync_ms={warmSyncElapsed.TotalMilliseconds:F1} "
            + $"cold_entries={coldSync.EntriesScanned} warm_entries={warmSync.EntriesScanned} "
            + $"cold_bytes={coldSync.BytesCopied} warm_bytes={warmSync.BytesCopied} "
            + $"workspace_bytes={warmSync.WorkspaceCandidateBytes} dependency_bytes={warmSync.DependencyCandidateBytes} "
            + $"run_ms={runElapsed.TotalMilliseconds:F1}");
    }

    private static async Task<string> ProbeVersionAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var runner = new TestProcessRunner(new TestProcessRunnerOptions
        {
            OutputStallTimeout = TimeSpan.FromSeconds(15),
            MaxCapturedCharactersPerStream = 128 * 1024,
        });
        TestProcessResult result = await runner.RunAsync(
            new TestProcessCommand(fileName, arguments, workingDirectory),
            timeout.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.StandardOutputTruncated);
        Assert.False(result.StandardErrorTruncated);
        string version = string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }
                .Where(output => !string.IsNullOrWhiteSpace(output)))
            .Trim();
        Assert.False(string.IsNullOrWhiteSpace(version));
        return version.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }

    private static string HashTree(string root, string buildDirectoryName = "ct-jvm-scale")
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !path.Contains(Path.Combine(".miller", buildDirectoryName), StringComparison.Ordinal)))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path)));
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
