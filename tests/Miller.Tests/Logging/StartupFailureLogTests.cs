using System.Globalization;
using System.Text.Json;
using Miller.Server.Logging;
using Xunit;

namespace Miller.Tests.Logging;

/// <summary>
/// Pins the last-resort startup record (<see cref="StartupFailureLog"/>) — the only channel that reports a
/// failure above <c>Program.cs</c>'s logger assignment. stderr must ALWAYS receive the record, the daily-log
/// append must fall through to the first writable candidate, a candidate whose parent is gone must be skipped
/// rather than recreated, and nothing here may throw. Temp-dir file I/O (Server-layer) → default suite.
/// </summary>
public sealed class StartupFailureLogTests : IDisposable
{
    private readonly string _root;

    public StartupFailureLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miller-startupfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly DateTimeOffset When =
        new(2026, 8, 25, 7, 38, 40, 123, TimeSpan.Zero);

    private string Candidate(string name)
    {
        string parent = Path.Combine(_root, name);
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, "logs");
    }

    private static Exception Failure()
    {
        try
        {
            throw new UnauthorizedAccessException("Access to the path is denied.");
        }
        catch (UnauthorizedAccessException caught)
        {
            return caught;
        }
    }

    [Fact]
    public void WritesTheRecordToStandardErrorAndNamesTheStage()
    {
        var stderr = new StringWriter();

        StartupFailureLog.Write(Failure(), "create-logs-dir", [Candidate("a")], stderr, When, 4242);

        string written = stderr.ToString();
        Assert.Contains("startup failed at stage 'create-logs-dir'", written, StringComparison.Ordinal);
        Assert.Contains("pid 4242", written, StringComparison.Ordinal);
        Assert.Contains("type=System.UnauthorizedAccessException", written, StringComparison.Ordinal);
        Assert.Contains("Access to the path is denied.", written, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendsOneFlattenedLineToTheSharedDailyPair()
    {
        string directory = Candidate("a");

        string? used = StartupFailureLog.Write(Failure(), "build-host", [directory], new StringWriter(), When, 7);

        Assert.Equal(directory, used);
        string[] human = File.ReadAllLines(Path.Combine(directory, "miller-20260825.log"));
        Assert.Single(human);
        Assert.StartsWith("07:38:40.123 [FTL] (role:startup pid:7 cid:) Miller.Startup:", human[0], StringComparison.Ordinal);

        string[] json = File.ReadAllLines(Path.Combine(directory, "miller-20260825.jsonl"));
        Assert.Single(json);
        using var parsed = JsonDocument.Parse(json[0]);
        Assert.Equal("Fatal", parsed.RootElement.GetProperty("@l").GetString());
        Assert.Equal("startup", parsed.RootElement.GetProperty("role").GetString());
        Assert.Equal(7, parsed.RootElement.GetProperty("pid").GetInt32());
        Assert.Contains("build-host", parsed.RootElement.GetProperty("@m").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsThroughToTheFirstWritableCandidate()
    {
        string missingParent = Path.Combine(_root, "gone", "logs");
        string writable = Candidate("second");

        string? used = StartupFailureLog.Write(
            Failure(), "build-logger", [missingParent, writable], new StringWriter(), When, 9);

        Assert.Equal(writable, used);
        Assert.False(Directory.Exists(missingParent));
        Assert.True(File.Exists(Path.Combine(writable, "miller-20260825.log")));
    }

    [Fact]
    public void NeverRecreatesACandidateWhoseParentIsGone()
    {
        string missingParent = Path.Combine(_root, "removed-root", ".miller", "logs");
        var stderr = new StringWriter();

        string? used = StartupFailureLog.Write(Failure(), "run-host", [missingParent], stderr, When, 11);

        Assert.Null(used);
        Assert.False(Directory.Exists(Path.Combine(_root, "removed-root")));
        Assert.Contains("startup failed at stage 'run-host'", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnsNormallyWhenEveryCandidateFails()
    {
        string blocked = Path.Combine(_root, "file-not-directory");
        File.WriteAllText(blocked, "x");

        string? used = StartupFailureLog.Write(
            Failure(), "resolve-workspace", [Path.Combine(blocked, "logs")], new StringWriter(), When, 13);

        Assert.Null(used);
    }

    [Fact]
    public void StillReportsToStandardErrorWhenTheCandidateListIsEmpty()
    {
        var stderr = new StringWriter();

        string? used = StartupFailureLog.Write(Failure(), "cli-dispatch", [], stderr, When, 15);

        Assert.Null(used);
        Assert.Contains("cli-dispatch", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsTheWholeRecordOnOneLine()
    {
        string directory = Candidate("single-line");

        StartupFailureLog.Write(Failure(), "build-host", [directory], new StringWriter(), When, 17);

        Assert.Single(File.ReadAllLines(Path.Combine(directory, "miller-20260825.log")));
    }

    [Fact]
    public void CandidateDirectoriesPrefersTheResolvedPathThenTheMachineGlobalThenTemp()
    {
        string millerDirectory = Path.Combine(_root, ".miller");

        IReadOnlyList<string> candidates = StartupFailureLog.CandidateDirectories(
            Path.Combine(_root, "workspace", ".miller", "logs"), millerDirectory);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(Path.Combine(_root, "workspace", ".miller", "logs"), candidates[0]);
        Assert.Equal(Path.Combine(millerDirectory, "logs"), candidates[1]);
        Assert.Equal(Path.GetTempPath(), candidates[2]);
    }

    [Fact]
    public void CandidateDirectoriesSkipsAnUnresolvedLogPathAndNeverRepeatsTheMachineGlobal()
    {
        string millerDirectory = Path.Combine(_root, ".miller");

        IReadOnlyList<string> unresolved = StartupFailureLog.CandidateDirectories(null, millerDirectory);
        IReadOnlyList<string> duplicate = StartupFailureLog.CandidateDirectories(
            Path.Combine(millerDirectory, "logs"), millerDirectory);

        Assert.Equal([Path.Combine(millerDirectory, "logs"), Path.GetTempPath()], unresolved);
        Assert.Equal([Path.Combine(millerDirectory, "logs"), Path.GetTempPath()], duplicate);
    }

    [Fact]
    public void RecordCarriesTheRunningMillerVersion()
    {
        var stderr = new StringWriter();

        StartupFailureLog.Write(Failure(), "build-logger", [], stderr, When, 19);

        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"miller {Miller.Server.MillerVersion.Current}"),
            stderr.ToString(),
            StringComparison.Ordinal);
    }
}
