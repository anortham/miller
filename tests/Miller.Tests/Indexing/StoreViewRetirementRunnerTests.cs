using Miller.Indexing;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreViewRetirementRunnerTests : IDisposable
{
    private static readonly Guid FamilyId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"miller-store-view-retirement-{Guid.NewGuid():N}");
    private readonly StoreSidecarReclaimTarget _target;
    private readonly string _storeRoot;
    private readonly string _argumentsPath;

    public StoreViewRetirementRunnerTests()
    {
        _storeRoot = Path.Combine(_root, "store");
        _argumentsPath = Path.Combine(_root, "arguments.txt");
        _target = new StoreSidecarReclaimTarget(FamilyId, "view-captured", _storeRoot);
        Directory.CreateDirectory(_storeRoot);
    }

    [Fact]
    public void Preview_UsesTheCapturedTargetWithoutApplyAndReturnsPlanned()
    {
        RequireUnix();
        string report = Report("plan", "planned", retiredViews: 1);
        string binary = WriteExecutable($"printf '%s\\n' \"$@\" > {ShellQuote(_argumentsPath)}\nprintf '%s\\n' {ShellQuote(report)}");

        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.Run(
            binary, _target, apply: false, timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(StoreViewRetirementDisposition.Planned, outcome.Disposition);
        Assert.Equal(FamilyId, outcome.FamilyId);
        Assert.Equal(_target.ViewId, outcome.ViewId);
        Assert.Equal(1, outcome.RetiredViews);
        Assert.Equal(1, outcome.RetiredManifests);
        Assert.Equal(1, outcome.RetiredManifestEntries);
        Assert.Null(outcome.Error);
        Assert.Equal(
            [
                "store",
                "maintain",
                "retire-view",
                "--store",
                _storeRoot,
                "--family",
                FamilyId.ToString("D"),
                "--view",
                _target.ViewId,
                "--json",
            ],
            File.ReadAllLines(_argumentsPath));
    }

    [Fact]
    public void Apply_IncludesApplyAndReturnsRetired()
    {
        RequireUnix();
        string report = Report("apply", "applied", retiredViews: 1);
        string binary = WriteExecutable($"printf '%s\\n' \"$@\" > {ShellQuote(_argumentsPath)}\nprintf '%s\\n' {ShellQuote(report)}");

        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.Run(
            binary, _target, apply: true, timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(StoreViewRetirementDisposition.Retired, outcome.Disposition);
        Assert.Equal(1, outcome.RetiredViews);
        Assert.Equal(1, outcome.RetiredManifests);
        Assert.Equal(1, outcome.RetiredManifestEntries);
        Assert.Contains("--apply", File.ReadAllLines(_argumentsPath));
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void ReadReport_ExposesEveryRetiredCount()
    {
        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.ReadReport(
            Report("plan", "planned", retiredViews: 1, retiredManifests: 2, retiredManifestEntries: 3),
            _target,
            apply: false);

        Assert.Equal(StoreViewRetirementDisposition.Planned, outcome.Disposition);
        Assert.Equal(1, outcome.RetiredViews);
        Assert.Equal(2, outcome.RetiredManifests);
        Assert.Equal(3, outcome.RetiredManifestEntries);
    }

    [Fact]
    public void ReadReport_CarriesNonnegativeFailureCounts()
    {
        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.ReadReport(
            FailureReport(
                "plan",
                FamilyId,
                "store_busy",
                "store is busy",
                retiredViews: 0,
                retiredManifests: 2,
                retiredManifestEntries: 3),
            _target,
            apply: false);

        Assert.Equal(StoreViewRetirementDisposition.Failed, outcome.Disposition);
        Assert.Equal(0, outcome.RetiredViews);
        Assert.Equal(2, outcome.RetiredManifests);
        Assert.Equal(3, outcome.RetiredManifestEntries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ReadReport_RequiresExactlyOneRetiredView(long retiredViews)
    {
        StoreViewRetirementOutcome planned = StoreViewRetirementRunner.ReadReport(
            Report("plan", "planned", retiredViews),
            _target,
            apply: false);
        StoreViewRetirementOutcome applied = StoreViewRetirementRunner.ReadReport(
            Report("apply", "applied", retiredViews),
            _target,
            apply: true);

        Assert.Equal(StoreViewRetirementDisposition.Failed, planned.Disposition);
        Assert.Equal(StoreViewRetirementDisposition.Failed, applied.Disposition);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void ReadReport_RejectsNegativeRetiredCounts(
        long retiredViews,
        long retiredManifests,
        long retiredManifestEntries)
    {
        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.ReadReport(
            FailureReport(
                "plan",
                FamilyId,
                "store_busy",
                "store is busy",
                retiredViews,
                retiredManifests,
                retiredManifestEntries),
            _target,
            apply: false);

        Assert.Equal(StoreViewRetirementDisposition.Failed, outcome.Disposition);
    }

    [Fact]
    public void ExactViewNotFound_IsAlreadyAbsent()
    {
        string report = FailureReport(
            "plan",
            FamilyId,
            "view_not_found",
            $"store has no view {_target.ViewId}");
        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.ReadReport(
            report, _target, apply: false);

        Assert.Equal(StoreViewRetirementDisposition.AlreadyAbsent, outcome.Disposition);
        Assert.Equal(FamilyId, outcome.FamilyId);
        Assert.Equal(_target.ViewId, outcome.ViewId);
        Assert.Equal(0, outcome.RetiredViews);
        Assert.Equal(0, outcome.RetiredManifests);
        Assert.Equal(0, outcome.RetiredManifestEntries);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void ViewNotFoundForAnotherView_IsFailed()
    {
        string report = FailureReport("plan", FamilyId, "view_not_found", "store has no view another-view");
        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.ReadReport(
            report, _target, apply: false);

        Assert.Equal(StoreViewRetirementDisposition.Failed, outcome.Disposition);
        Assert.Contains(_target.ViewId, outcome.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrong-family", "retire_view", "plan", "planned")]
    [InlineData("right-family", "other_action", "plan", "planned")]
    [InlineData("right-family", "retire_view", "apply", "planned")]
    [InlineData("right-family", "retire_view", "plan", "applied")]
    public void WrongIdentityOrContractFields_AreFailed(
        string family,
        string action,
        string mode,
        string disposition)
    {
        Guid reportFamily = family == "right-family"
            ? FamilyId
            : Guid.Parse("22222222-2222-4222-8222-222222222222");
        string report = $$"""
            {"report_schema_version":1,"action":"{{action}}","mode":"{{mode}}","family_id":"{{reportFamily:D}}","view_id":"{{_target.ViewId}}","disposition":"{{disposition}}","counts":{"retired_views":1},"failure_class":"none","error":null}
            """;
        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.ReadReport(
            report, _target, apply: mode == "apply");

        Assert.Equal(StoreViewRetirementDisposition.Failed, outcome.Disposition);
        Assert.Equal(FamilyId, outcome.FamilyId);
        Assert.Equal(_target.ViewId, outcome.ViewId);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public void NonzeroExit_IsFailedWithTheProducerDiagnostic()
    {
        RequireUnix();
        string binary = WriteExecutable("printf '%s\\n' 'store busy' >&2\nexit 17");

        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.Run(
            binary, _target, apply: false, timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(StoreViewRetirementDisposition.Failed, outcome.Disposition);
        Assert.Contains("exited 17", outcome.Error, StringComparison.Ordinal);
        Assert.Contains("store busy", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void NonzeroExitCarryingAViewNotFoundReport_IsAlreadyAbsent()
    {
        RequireUnix();
        string report = FailureReport(
            "plan",
            FamilyId,
            "view_not_found",
            $"store has no view {_target.ViewId}");
        string binary = WriteExecutable($"printf '%s\\n' {ShellQuote(report)}\nexit 1");

        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.Run(
            binary, _target, apply: false, timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(StoreViewRetirementDisposition.AlreadyAbsent, outcome.Disposition);
        Assert.Null(outcome.Error);
    }

    [Theory]
    [InlineData("planned")]
    [InlineData("applied")]
    public void ASuccessReportFromAProcessThatDied_IsFailedNotARetirement(string disposition)
    {
        RequireUnix();
        string report = Report("plan", disposition, retiredViews: 1);
        string binary = WriteExecutable($"printf '%s\\n' {ShellQuote(report)}\nexit 137");

        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.Run(
            binary, _target, apply: false, timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(StoreViewRetirementDisposition.Failed, outcome.Disposition);
        Assert.Contains("exited 137", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void NonzeroExitCarryingAReport_ReportsTheProducersOwnErrorNotAnEmptyStderr()
    {
        RequireUnix();
        string report = FailureReport("plan", FamilyId, "store_locked", "another writer holds the store");
        string binary = WriteExecutable($"printf '%s\\n' {ShellQuote(report)}\nexit 1");

        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.Run(
            binary, _target, apply: false, timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(StoreViewRetirementDisposition.Failed, outcome.Disposition);
        Assert.Contains("another writer holds the store", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedReport_IsFailed()
    {
        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.ReadReport(
            "not json", _target, apply: false);

        Assert.Equal(StoreViewRetirementDisposition.Failed, outcome.Disposition);
        Assert.Equal("store view retirement emitted an unreadable report", outcome.Error);
    }

    [Fact]
    public void Timeout_KillsTheProducerAndIsFailed()
    {
        RequireUnix();
        string completedPath = Path.Combine(_root, "completed.txt");
        string binary = WriteExecutable(
            $"printf '%s\\n' started > {ShellQuote(_argumentsPath)}\nsleep 0.5\nprintf '%s\\n' completed > {ShellQuote(completedPath)}");

        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.Run(
            binary, _target, apply: false, timeout: TimeSpan.FromMilliseconds(20));

        Assert.Equal(StoreViewRetirementDisposition.Failed, outcome.Disposition);
        Assert.Equal("store view retirement timed out", outcome.Error);
        Thread.Sleep(TimeSpan.FromMilliseconds(800));
        Assert.False(File.Exists(completedPath));
    }

    [Fact]
    public void ForToolsRoot_BindsThePinnedExtractor()
    {
        RequireUnix();
        string toolsRoot = Path.Combine(_root, "tools");
        Directory.CreateDirectory(toolsRoot);
        string binary = Path.Combine(toolsRoot, "julie-extract");
        File.WriteAllText(binary, $"#!/bin/sh\nprintf '%s\\n' {ShellQuote(Report("plan", "planned", 1))}");
        SetExecutable(binary);

        Func<StoreSidecarReclaimTarget, bool, StoreViewRetirementOutcome>? callback =
            StoreViewRetirementRunner.ForToolsRoot(toolsRoot);

        Assert.NotNull(callback);
        StoreViewRetirementOutcome outcome = callback!(_target, false);
        Assert.Equal(StoreViewRetirementDisposition.Planned, outcome.Disposition);
    }

    [Fact]
    public void MissingBinary_IsFailedWithoutThrowing()
    {
        StoreViewRetirementOutcome outcome = StoreViewRetirementRunner.Run(
            Path.Combine(_root, "missing-extractor"), _target, apply: false, timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(StoreViewRetirementDisposition.Failed, outcome.Disposition);
        Assert.Equal(FamilyId, outcome.FamilyId);
        Assert.Equal(_target.ViewId, outcome.ViewId);
        Assert.NotNull(outcome.Error);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string Report(
        string mode,
        string disposition,
        long retiredViews,
        long retiredManifests = 1,
        long retiredManifestEntries = 1) =>
        $"{{\"report_schema_version\":1,\"action\":\"retire_view\",\"mode\":\"{mode}\",\"family_id\":\"{FamilyId:D}\",\"disposition\":\"{disposition}\",\"counts\":{{\"retired_views\":{retiredViews},\"retired_manifests\":{retiredManifests},\"retired_manifest_entries\":{retiredManifestEntries}}},\"failure_class\":\"none\",\"error\":null}}";

    private string FailureReport(
        string mode,
        Guid familyId,
        string code,
        string message,
        long retiredViews = 0,
        long retiredManifests = 0,
        long retiredManifestEntries = 0) =>
        $"{{\"report_schema_version\":1,\"action\":\"retire_view\",\"mode\":\"{mode}\",\"family_id\":\"{familyId:D}\",\"view_id\":\"{_target.ViewId}\",\"disposition\":\"failed\",\"counts\":{{\"retired_views\":{retiredViews},\"retired_manifests\":{retiredManifests},\"retired_manifest_entries\":{retiredManifestEntries}}},\"failure_class\":\"invalid_arguments\",\"error\":{{\"class\":\"invalid_arguments\",\"code\":\"{code}\",\"message\":\"{message}\"}}}}";

    private string WriteExecutable(string body)
    {
        string path = Path.Combine(_root, $"julie-extract-{Guid.NewGuid():N}");
        File.WriteAllText(path, $"#!/bin/sh\nset -eu\n{body}\n");
        SetExecutable(path);
        return path;
    }

    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\\"'\\\"'")}'";

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("The fake producer uses a POSIX executable.");
    }
}
