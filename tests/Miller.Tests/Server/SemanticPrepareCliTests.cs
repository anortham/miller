using System.Text.Json;
using Miller.Indexing.Semantic;
using Miller.Server;
using Miller.Server.Cli;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>miller semantic prepare</c> consent verb. Every case runs the pure core with an injected process
/// runner — NO real sidecar is spawned in the fast suite — so the gates prove the consent/marker/exit contracts:
/// the marker exists exactly while the child runs, a missing binary or a disk-blocked preflight refuses BEFORE
/// spawning, and the child's exit code passes straight through.
/// </summary>
public sealed class SemanticPrepareCliTests : IDisposable
{
    private const int FakePid = 4242;
    private static readonly DateTimeOffset FakeNow = new(2026, 7, 20, 18, 30, 0, TimeSpan.Zero);

    private readonly string _millerDir;
    private readonly string _toolsRoot;

    public SemanticPrepareCliTests()
    {
        string unique = Guid.NewGuid().ToString("N");
        _millerDir = Path.Combine(Path.GetTempPath(), "miller-prepare-" + unique, ".miller");
        _toolsRoot = Path.Combine(Path.GetTempPath(), "miller-prepare-tools-" + unique);
        Directory.CreateDirectory(_millerDir);
        Directory.CreateDirectory(_toolsRoot);
    }

    public void Dispose()
    {
        TryDelete(Path.GetDirectoryName(_millerDir)!);
        TryDelete(_toolsRoot);
    }

    private string MarkerPath => Path.Combine(_millerDir, SemanticPrepareCli.MarkerFileName);

    [Fact]
    public void Prepare_CreatesMarkerBeforeSpawn_RecordingModelAndPid_AndDeletesOnSuccess()
    {
        string? markerDuringSpawn = null;
        var cli = Build(binaryExists: true, preflight: Ok(), runner: (_, _, stdout, _) =>
        {
            markerDuringSpawn = File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath) : null;
            stdout.WriteLine("downloading 42%");
            return 0;
        });

        var (code, _, _) = Run(cli, new SemanticPrepareRequest("qwen3-0.6b-f16", Json: false));

        Assert.Equal(0, code);
        Assert.NotNull(markerDuringSpawn);
        using JsonDocument marker = JsonDocument.Parse(markerDuringSpawn!);
        Assert.Equal("qwen3-0.6b-f16", marker.RootElement.GetProperty("model").GetString());
        Assert.Equal(FakePid, marker.RootElement.GetProperty("pid").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(marker.RootElement.GetProperty("createdUtc").GetString()));
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void Prepare_WithoutModel_RecordsActiveEncoderIdInMarker()
    {
        string? markerDuringSpawn = null;
        var cli = Build(binaryExists: true, preflight: Ok(), runner: (_, _, _, _) =>
        {
            markerDuringSpawn = File.ReadAllText(MarkerPath);
            return 0;
        });

        Run(cli, new SemanticPrepareRequest(Model: null, Json: false));

        using JsonDocument marker = JsonDocument.Parse(markerDuringSpawn!);
        Assert.Equal(
            MillerSemanticContract.DefaultEncoder.ModelId,
            marker.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void Prepare_DeletesMarker_WhenSidecarFails()
    {
        var cli = Build(binaryExists: true, preflight: Ok(), runner: (_, _, _, _) => 7);

        var (code, _, _) = Run(cli, new SemanticPrepareRequest(Model: null, Json: false));

        Assert.Equal(7, code);
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void Prepare_DeletesMarker_WhenRunnerThrows()
    {
        var cli = Build(binaryExists: true, preflight: Ok(),
            runner: (_, _, _, _) => throw new InvalidOperationException("boom"));

        Assert.Throws<InvalidOperationException>(() =>
            Run(cli, new SemanticPrepareRequest(Model: null, Json: false)));

        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void Prepare_DoesNotDeleteMarker_OwnedByConcurrentInvocation()
    {
        using var secondMarkerWritten = new ManualResetEventSlim(false);
        using var secondMayFinish = new ManualResetEventSlim(false);

        var second = Build(binaryExists: true, preflight: Ok(), pid: 9999, nonce: "second-nonce",
            runner: (_, _, _, _) =>
            {
                secondMarkerWritten.Set();
                secondMayFinish.Wait();
                return 0;
            });

        Thread? secondThread = null;
        var first = Build(binaryExists: true, preflight: Ok(), pid: FakePid, nonce: "first-nonce",
            runner: (_, _, _, _) =>
            {
                secondThread = new Thread(() => second.Run(
                    new SemanticPrepareRequest("second-model", Json: false),
                    _toolsRoot, _millerDir, new StringWriter(), new StringWriter()));
                secondThread.Start();
                secondMarkerWritten.Wait();
                return 0;
            });

        Run(first, new SemanticPrepareRequest("first-model", Json: false));

        Assert.True(File.Exists(MarkerPath));
        using (JsonDocument marker = JsonDocument.Parse(File.ReadAllText(MarkerPath)))
        {
            Assert.Equal("second-model", marker.RootElement.GetProperty("model").GetString());
            Assert.Equal(9999, marker.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal("second-nonce", marker.RootElement.GetProperty("nonce").GetString());
        }

        secondMayFinish.Set();
        secondThread!.Join();
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void Prepare_PassesExitCodeThrough()
    {
        var cli = Build(binaryExists: true, preflight: Ok(), runner: (_, _, _, _) => 3);

        var (code, _, _) = Run(cli, new SemanticPrepareRequest(Model: null, Json: false));

        Assert.Equal(3, code);
    }

    [Fact]
    public void Prepare_ForwardsOnlyModelToTheSidecar_NeverMillerJsonFlag()
    {
        IReadOnlyList<string>? captured = null;
        var cli = Build(binaryExists: true, preflight: Ok(), runner: (_, args, _, _) =>
        {
            captured = args;
            return 0;
        });

        Run(cli, new SemanticPrepareRequest("custom-id", Json: true));

        Assert.Equal(new[] { "prepare", "--model", "custom-id" }, captured);
    }

    [Fact]
    public void Prepare_ForwardsResolvedActiveEncoder_WhenNoModelGiven()
    {
        IReadOnlyList<string>? captured = null;
        var cli = Build(binaryExists: true, preflight: Ok(), runner: (_, args, _, _) =>
        {
            captured = args;
            return 0;
        });

        Run(cli, new SemanticPrepareRequest(Model: null, Json: false));

        Assert.Equal(
            new[] { "prepare", "--model", MillerSemanticContract.DefaultEncoder.ModelId },
            captured);
    }

    [Fact]
    public void MissingBinary_FailsLoud_WithRestoreScriptMessage_NoSpawn_NoMarker()
    {
        bool spawned = false;
        var cli = Build(binaryExists: false, preflight: Ok(), runner: (_, _, _, _) =>
        {
            spawned = true;
            return 0;
        });

        var (code, _, err) = Run(cli, new SemanticPrepareRequest(Model: null, Json: false));

        Assert.Equal(3, code);
        Assert.False(spawned);
        Assert.False(File.Exists(MarkerPath));
        Assert.Contains("julie-semantic-sidecar", err);
        Assert.Contains("restore", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingBinary_Json_EmitsMachineReadableRefusalOnStdout()
    {
        var cli = Build(binaryExists: false, preflight: Ok(), runner: (_, _, _, _) => 0);

        var (code, outText, err) = Run(cli, new SemanticPrepareRequest(Model: null, Json: true));

        Assert.Equal(3, code);
        Assert.Empty(err);
        using JsonDocument json = JsonDocument.Parse(outText);
        Assert.Equal("sidecar_missing", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void PreflightBlocked_ShortCircuits_NoSpawn_NoMarker_WithActionableMessage()
    {
        bool spawned = false;
        var cli = Build(binaryExists: true, preflight: Blocked(freeBytes: 100, requiredBytes: 2000),
            runner: (_, _, _, _) =>
            {
                spawned = true;
                return 0;
            });

        var (code, _, err) = Run(cli, new SemanticPrepareRequest(Model: null, Json: false));

        Assert.Equal(3, code);
        Assert.False(spawned);
        Assert.False(File.Exists(MarkerPath));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void PreflightBlocked_Json_CarriesFreeAndRequiredBytes()
    {
        var cli = Build(binaryExists: true, preflight: Blocked(freeBytes: 100, requiredBytes: 2000),
            runner: (_, _, _, _) => 0);

        var (code, outText, _) = Run(cli, new SemanticPrepareRequest(Model: null, Json: true));

        Assert.Equal(3, code);
        using JsonDocument json = JsonDocument.Parse(outText);
        Assert.Equal("disk_blocked", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(100, json.RootElement.GetProperty("free_bytes").GetInt64());
        Assert.Equal(2000, json.RootElement.GetProperty("required_bytes").GetInt64());
    }

    [Fact]
    public void Dispatch_SemanticWithoutOperation_IsUsageError()
    {
        var (code, _, err) = Dispatch("semantic");

        Assert.Equal(2, code);
        Assert.Contains("semantic prepare", err);
    }

    [Fact]
    public void Dispatch_UnknownSemanticOperation_IsUsageError()
    {
        var (code, _, err) = Dispatch("semantic", "bogus");

        Assert.Equal(2, code);
        Assert.Contains("unknown semantic operation", err);
    }

    [Fact]
    public void Dispatch_UnknownOption_IsUsageError()
    {
        var (code, _, err) = Dispatch("semantic", "prepare", "--nope");

        Assert.Equal(2, code);
        Assert.Contains("unknown option", err);
    }

    [Fact]
    public void Dispatch_PrepareWithMissingSidecar_ReturnsOperationalFailure()
    {
        var (code, _, err) = Dispatch("semantic", "prepare");

        Assert.Equal(3, code);
        Assert.Contains("julie-semantic-sidecar", err);
    }

    [Fact]
    public void Help_DocumentsTheSemanticVerb()
    {
        var (code, outText, _) = Dispatch("help");

        Assert.Equal(0, code);
        Assert.Contains("semantic", outText);
        Assert.Contains("prepare", outText);
    }

    private SemanticPrepareCli Build(
        bool binaryExists,
        ISemanticPreparePreflight preflight,
        SemanticPrepareProcessRunner runner,
        int pid = FakePid,
        string nonce = "test-nonce") =>
        new(_ => binaryExists, preflight, runner, () => pid, () => FakeNow, () => nonce);

    private (int Code, string Out, string Err) Run(SemanticPrepareCli cli, SemanticPrepareRequest request)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = cli.Run(request, _toolsRoot, _millerDir, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (int Code, string Out, string Err) Dispatch(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliDispatch.Run(args, Context(), stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private WorkspaceContext Context() => new(
        Path.GetDirectoryName(_millerDir)!,
        Path.Combine(_millerDir, "symbols.db"),
        Path.Combine(_millerDir, "telemetry.db"),
        Path.Combine(_millerDir, "workspaces.db"),
        _toolsRoot,
        WorkspaceId: null);

    private static ISemanticPreparePreflight Ok() =>
        new StubPreflight(new SemanticPreparePreflightResult(true, long.MaxValue, 0, null));

    private static ISemanticPreparePreflight Blocked(long freeBytes, long requiredBytes) =>
        new StubPreflight(new SemanticPreparePreflightResult(false, freeBytes, requiredBytes, null));

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class StubPreflight(SemanticPreparePreflightResult result) : ISemanticPreparePreflight
    {
        public SemanticPreparePreflightResult Check(string cacheDir) => result;
    }
}
