using Miller.Testing;
using Miller.Testing.Parsing;
using Miller.Testing.Providers.Shared;
using System.Diagnostics;

namespace Miller.Testing.Providers.Godot;

public sealed class GodotTestProvider : IContinuousTestProvider
{
    private readonly ITestProcessRunner _runner;

    public GodotTestProvider(ITestProcessRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
        ContinuousTestWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!IsGodotProjectFile(workspace.ProjectPath))
            throw new ContinuousTestProviderException(
                $"Godot continuous testing requires project.godot: '{workspace.ProjectPath}'.");

        try
        {
            GodotProjectShadowResult shadow = GodotProjectShadow.Sync(workspace, cancellationToken);
            GutConfiguration configuration = GutConfiguration.Load(
                Path.Combine(shadow.ProjectMirrorRoot, ".gutconfig.json"));
            IReadOnlyList<GutScript> scripts = configuration.DiscoverScripts(shadow.ProjectMirrorRoot);
            return Task.FromResult<IReadOnlyList<ProviderTestCase>>(
                scripts.Select(script => ToProviderCase(workspace, shadow, script)).ToArray());
        }
        catch (IOException exception)
        {
            throw new ContinuousTestProviderException(exception.Message, exception);
        }
    }

    public async Task<ProviderRunResult> RunAsync(
        ContinuousTestProviderRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CoverageMode != ContinuousTestCoverageMode.None)
            throw new ContinuousTestProviderException(
                "Godot continuous testing does not support coverage instrumentation.");
        if (!request.WholeSuite && request.TestCaseIds.Count == 0)
            throw new ContinuousTestProviderException(
                "GUT run request selected no test case IDs; an empty selection cannot be reported green.");
        if (!IsGodotProjectFile(request.Workspace.ProjectPath))
            throw new ContinuousTestProviderException(
                $"Godot continuous testing requires project.godot: '{request.Workspace.ProjectPath}'.");

        CtGenerationPaths paths = CtGenerationPaths.Allocate(request.Workspace);
        try
        {
            paths.EnsureDirectories();
            GodotProjectShadowResult shadow = GodotProjectShadow.Sync(
                request.Workspace,
                cancellationToken);
            GutConfiguration configuration = GutConfiguration.Load(
                Path.Combine(shadow.ProjectMirrorRoot, ".gutconfig.json"));
            IReadOnlyList<GutScript> discovered = configuration.DiscoverScripts(shadow.ProjectMirrorRoot);
            IReadOnlyList<GutScript> selected = SelectScripts(request, discovered);
            if (selected.Count == 0)
            {
                if (!request.WholeSuite)
                    throw new ContinuousTestProviderException(
                        "GUT run request selected no discovered test scripts; an empty selection cannot be reported green.");
                return new ProviderRunResult(
                    RunId: request.RunId ?? NewRunId(request, paths.GenerationId),
                    Status: "passed",
                    StartedAt: DateTimeOffset.UtcNow,
                    EndedAt: DateTimeOffset.UtcNow)
                {
                    GenerationId = paths.GenerationId,
                };
            }

            GutTooling.EnsureSupportedGutProject(shadow);
            string godot = GutTooling.ResolveGodotExecutable();
            var metrics = new RunMetrics(shadow);
            TestProcessResult version = await RunGodotProcessAsync(
                _runner,
                "version",
                GutTooling.BuildVersionCommand(godot, shadow),
                shadow,
                metrics,
                cancellationToken).ConfigureAwait(false);
            if (version.ExitCode != 0)
                throw Failure(
                    $"Godot version probe failed with exit code {version.ExitCode}: {FailureSummary(version)}");
            if (GutTooling.ParseGodotMajor(version) < GutTooling.MinimumGodotMajor)
                throw Failure("Godot 4 or newer is required for GUT continuous testing.");

            bool imported = false;
            if (GodotProjectShadow.NeedsImport(shadow))
            {
                TestProcessResult import = await RunGodotProcessAsync(
                    _runner,
                    "import",
                    GutTooling.BuildImportCommand(godot, shadow),
                    shadow,
                    metrics,
                    cancellationToken).ConfigureAwait(false);
                if (import.ExitCode != 0)
                    throw Failure(
                        $"Godot project import failed with exit code {import.ExitCode}: {FailureSummary(import)}");
                GodotProjectShadow.PublishImportStamp(shadow, DateTimeOffset.UtcNow);
                imported = true;
            }

            string resultsRoot = Path.Combine(shadow.ProjectMirrorRoot, ".miller-gut-results");
            ClearResults(resultsRoot);
            const string reportResPath = "res://.miller-gut-results/run.xml";
            string configPath = Path.Combine(resultsRoot, "miller.gutconfig.json");
            WriteAtomic(
                configPath,
                configuration.SerializeDerived(selected.Select(script => script.ResPath), reportResPath));
            TestProcessResult gut = await RunGodotProcessAsync(
                _runner,
                "gut",
                GutTooling.BuildRunCommand(
                    godot,
                    shadow,
                    "res://.miller-gut-results/miller.gutconfig.json",
                    reportResPath),
                shadow,
                metrics,
                cancellationToken).ConfigureAwait(false);

            var reportCopyTimer = Stopwatch.StartNew();
            string artifactPath;
            try
            {
                artifactPath = CopyReport(shadow, paths, reportResPath);
            }
            finally
            {
                reportCopyTimer.Stop();
                metrics.ReportCopyDurationMs = reportCopyTimer.Elapsed.TotalMilliseconds;
            }
            JUnitXmlParseResult report;
            try
            {
                report = JUnitXmlResultParser.ParseFile(artifactPath);
            }
            catch (TestArtifactParseException exception)
            {
                throw Failure("GUT JUnit report was malformed: " + exception.Message, artifactPath, exception);
            }
            if (report.HasAggregateMismatch || report.Diagnostics.Count != 0)
                throw Failure(
                    "GUT JUnit report aggregate or diagnostic evidence was inconsistent: "
                    + string.Join(
                        " ",
                        report.AggregateMismatches.Select(mismatch => mismatch.ToString())
                            .Concat(report.Diagnostics)),
                    artifactPath);

            IReadOnlyList<ProviderCaseResult> results = MapResults(
                request,
                paths,
                selected,
                report,
                gut.ExitCode,
                artifactPath,
                imported,
                metrics);
            return new ProviderRunResult(
                RunId: request.RunId ?? NewRunId(request, paths.GenerationId),
                Status: AggregateStatus(results.Select(result => result.Status)),
                StartedAt: DateTimeOffset.UtcNow,
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults: results,
                ResultArtifactPath: artifactPath,
                TestDisplayNames: selected.Select(script => script.ResPath).ToArray())
            {
                GenerationId = paths.GenerationId,
            };
        }
        catch (ContinuousTestProviderException exception) when (exception.GenerationId is null)
        {
            throw StampGeneration(exception, paths);
        }
        catch (IOException exception)
        {
            throw StampGeneration(
                new ContinuousTestProviderException(exception.Message, exception),
                paths);
        }
    }

    public static bool IsGodotProjectFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return string.Equals(Path.GetFileName(path), "project.godot", StringComparison.OrdinalIgnoreCase);
    }

    private static ProviderTestCase ToProviderCase(
        ContinuousTestWorkspace workspace,
        GodotProjectShadowResult shadow,
        GutScript script)
    {
        string sourcePath = Path.GetRelativePath(
                workspace.WorkspaceRoot,
                Path.Combine(shadow.SourceRoot, script.ResPath[6..].Replace('/', Path.DirectorySeparatorChar)))
            .Replace('\\', '/');
        return new ProviderTestCase(
            Id: "gut:" + script.ResPath,
            DisplayName: script.ResPath,
            FullyQualifiedName: script.ResPath,
            Selector: script.ResPath,
            Framework: GutTooling.Framework,
            SourcePath: sourcePath,
            Metadata: new Dictionary<string, object?>
            {
                ["kind"] = "gut-script",
                ["script_path"] = script.ResPath,
            });
    }

    private static IReadOnlyList<GutScript> SelectScripts(
        ContinuousTestProviderRunRequest request,
        IReadOnlyList<GutScript> discovered)
    {
        if (request.WholeSuite && request.TestCaseIds.Count == 0)
            return discovered;

        var byId = discovered.ToDictionary(script => "gut:" + script.ResPath, StringComparer.Ordinal);
        var selected = new List<GutScript>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in request.TestCaseIds)
        {
            if (!id.StartsWith("gut:", StringComparison.Ordinal))
                throw new ContinuousTestProviderException($"GUT test case ID is malformed: '{id}'.");
            string normalized = GutTooling.NormalizeResPath(id[4..]);
            string canonicalId = "gut:" + normalized;
            if (!byId.TryGetValue(canonicalId, out GutScript? script))
                throw new ContinuousTestProviderException(
                    $"GUT test case ID was not discovered in the project: '{id}'.");
            if (seen.Add(canonicalId))
                selected.Add(script);
        }
        return selected.OrderBy(script => script.ResPath, StringComparer.Ordinal).ToArray();
    }

    private static async Task<TestProcessResult> RunGodotProcessAsync(
        ITestProcessRunner runner,
        string phase,
        TestProcessCommand command,
        GodotProjectShadowResult shadow,
        RunMetrics metrics,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            return await runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            timer.Stop();
            metrics.SetProcessDuration(phase, timer.Elapsed.TotalMilliseconds);
            GodotProjectShadow.TouchActivity(shadow);
            (long projectBytes, long homeBytes) = GodotProjectShadow.EnforcePostProcessBudget(shadow);
            metrics.ProjectCandidateBytes = projectBytes;
            metrics.GodotHomeCandidateBytes = homeBytes;
        }
    }

    private static IReadOnlyList<ProviderCaseResult> MapResults(
        ContinuousTestProviderRunRequest request,
        CtGenerationPaths paths,
        IReadOnlyList<GutScript> selected,
        JUnitXmlParseResult report,
        int exitCode,
        string artifactPath,
        bool imported,
        RunMetrics metrics)
    {
        var selectedByPath = selected.ToDictionary(script => script.ResPath, StringComparer.Ordinal);
        var rowsByPath = selected.ToDictionary(
            script => script.ResPath,
            _ => new List<JUnitXmlTestCase>(),
            StringComparer.Ordinal);
        var seenRows = new HashSet<string>(StringComparer.Ordinal);
        foreach (JUnitXmlTestCase row in report.Cases)
        {
            string scriptPath = ScriptPathFromReport(row);
            if (!selectedByPath.ContainsKey(scriptPath))
                throw Failure(
                    $"GUT JUnit row was not selected or could not be attributed: '{scriptPath}'.",
                    artifactPath);
            string rowKey = ReportRowKey(scriptPath, row);
            if (!seenRows.Add(rowKey))
                throw Failure($"GUT JUnit report contained a duplicate row for '{rowKey}'.", artifactPath);
            rowsByPath[scriptPath].Add(row);
        }

        if (rowsByPath.Any(pair => pair.Value.Count == 0))
            throw Failure("GUT JUnit report omitted one or more selected scripts.", artifactPath);
        if (exitCode is not (0 or 1))
            throw Failure($"GUT test process exited with unsupported code {exitCode}.", artifactPath);
        bool hasFailure = rowsByPath.Values
            .SelectMany(rows => rows)
            .Any(row => row.Status is "failed" or "errored");
        if (exitCode == 0 && hasFailure)
            throw Failure("GUT exited successfully but JUnit evidence contains a failure.", artifactPath);
        if (exitCode == 1 && !hasFailure)
            throw Failure("GUT exited with failure but JUnit evidence contains no failure.", artifactPath);

        var results = new List<ProviderCaseResult>(selected.Count);
        foreach (GutScript script in selected)
        {
            List<JUnitXmlTestCase> rows = rowsByPath[script.ResPath];
            string status = AggregateStatus(rows.Select(row => row.Status));
            double duration = rows
                .Where(row => row.DurationSeconds is not null)
                .Sum(row => row.DurationSeconds!.Value);
            JUnitXmlTestCase? failure = rows.FirstOrDefault(row => row.Status is "failed" or "errored");
            results.Add(new ProviderCaseResult(
                Id: CtStableIds.StableId(
                    "test_result",
                    request.Workspace.WorkspaceId,
                    "gut:" + script.ResPath,
                    request.RunId),
                TestCaseId: "gut:" + script.ResPath,
                Status: status,
                ResultRevision: request.SelectedRevision,
                IndexIdentity: request.IndexIdentity,
                DurationSeconds: duration,
                FailureSummary: failure?.FailureText ?? failure?.FailureMessage,
                Metadata: new Dictionary<string, object?>
                {
                    ["framework"] = GutTooling.Framework,
                    ["artifact_path"] = artifactPath,
                    ["imported"] = imported,
                    ["godot_project_candidate_bytes"] = metrics.ProjectCandidateBytes,
                    ["godot_home_candidate_bytes"] = metrics.GodotHomeCandidateBytes,
                    ["project_candidate_bytes"] = metrics.ProjectCandidateBytes,
                    ["godot_home_bytes"] = metrics.GodotHomeCandidateBytes,
                    ["mirror_elapsed_ms"] = metrics.Shadow.Elapsed.TotalMilliseconds,
                    ["source_metadata_digest"] = metrics.Shadow.SourceMetadataDigest,
                    ["version_duration_ms"] = metrics.VersionDurationMs,
                    ["import_duration_ms"] = metrics.ImportDurationMs,
                    ["gut_duration_ms"] = metrics.GutDurationMs,
                    ["report_copy_duration_ms"] = metrics.ReportCopyDurationMs,
                    ["mirror_entries_copied"] = metrics.Shadow.EntriesCopied,
                    ["mirror_entries_updated"] = metrics.Shadow.EntriesUpdated,
                    ["mirror_entries_deleted"] = metrics.Shadow.EntriesDeleted,
                    ["mirror_bytes_copied"] = metrics.Shadow.BytesCopied,
                    ["mirror_files_hashed"] = metrics.Shadow.FilesHashed,
                    ["mirror_bytes_hashed"] = metrics.Shadow.BytesHashed,
                    ["generation_id"] = paths.GenerationId,
                }));
        }
        return results;
    }

    private static string ScriptPathFromReport(JUnitXmlTestCase row)
    {
        foreach (string? candidate in new[] { row.ClassName, row.SuiteName })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            string value = candidate.Trim();
            int extension = value.IndexOf(".gd", StringComparison.OrdinalIgnoreCase);
            if (extension >= 0)
                value = value[..(extension + 3)];
            if (value.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
                return GutTooling.NormalizeResPath(value);
        }
        throw Failure($"GUT JUnit row has no attributable res:// script: '{row.Name}'.");
    }

    private static string ReportRowKey(string scriptPath, JUnitXmlTestCase row) =>
        scriptPath + "\x1f" + (row.ClassName ?? row.SuiteName) + "\x1f" + row.Name;

    private static void ClearResults(string resultsRoot)
    {
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(resultsRoot);
        if (Directory.Exists(resultsRoot))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         resultsRoot,
                         "*",
                         SearchOption.AllDirectories))
                CtWorkspaceMirror.EnsurePathHasNoReparsePoint(entry);
            Directory.Delete(resultsRoot, recursive: true);
        }
        Directory.CreateDirectory(resultsRoot);
    }

    private static string CopyReport(
        GodotProjectShadowResult shadow,
        CtGenerationPaths paths,
        string reportResPath)
    {
        string resultsRoot = Path.Combine(shadow.ProjectMirrorRoot, ".miller-gut-results");
        string expected = Path.Combine(
            shadow.ProjectMirrorRoot,
            reportResPath[6..].Replace('/', Path.DirectorySeparatorChar));
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(resultsRoot);
        if (!File.Exists(expected))
            throw Failure("GUT did not produce its expected JUnit report.");
        CtWorkspaceMirror.EnsurePathHasNoReparsePoint(expected);
        string[] reports = Directory
            .EnumerateFiles(resultsRoot, "*.xml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (reports.Length != 1
            || !string.Equals(
                Path.GetFullPath(reports[0]),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase))
            throw Failure("GUT produced an unexpected or duplicate JUnit report.");

        string destination = Path.Combine(paths.ResultsDirectory, "gut-junit.xml");
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(expected, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return destination;
    }

    private static void WriteAtomic(string path, string contents)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string AggregateStatus(IEnumerable<string> statuses)
    {
        HashSet<string> set = statuses
            .Select(status => status is "errored" ? "failed" : status)
            .ToHashSet(StringComparer.Ordinal);
        if (set.Contains("failed"))
            return "failed";
        return set.Count != 0 && set.SetEquals(["skipped"]) ? "skipped" : "passed";
    }

    private static string FailureSummary(TestProcessResult result) =>
        string.Join(
            " ",
            new[] { result.StandardError, result.StandardOutput }
                .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static ContinuousTestProviderException Failure(
        string message,
        string? artifactPath = null,
        Exception? innerException = null)
        => innerException is null
            ? new ContinuousTestProviderException(message) { ResultArtifactPath = artifactPath }
            : new ContinuousTestProviderException(message, innerException) { ResultArtifactPath = artifactPath };

    private static ContinuousTestProviderException StampGeneration(
        ContinuousTestProviderException exception,
        CtGenerationPaths paths) =>
        new(exception.Message, exception)
        {
            ResultArtifactPath = exception.ResultArtifactPath,
            GenerationId = paths.GenerationId,
        };

    private static string NewRunId(
        ContinuousTestProviderRunRequest request,
        string generationId) =>
        "ct-run:" + CtStableIds.StableId(
            "gut-run",
            request.Workspace.WorkspaceId,
            request.Workspace.ProjectPath,
            request.SelectedRevision,
            generationId)["gut-run:".Length..];

    private sealed class RunMetrics
    {
        internal RunMetrics(GodotProjectShadowResult shadow)
        {
            Shadow = shadow;
            ProjectCandidateBytes = shadow.ProjectCandidateBytes;
            GodotHomeCandidateBytes = shadow.GodotHomeCandidateBytes;
        }

        internal GodotProjectShadowResult Shadow { get; }

        internal long ProjectCandidateBytes { get; set; }

        internal long GodotHomeCandidateBytes { get; set; }

        internal double VersionDurationMs { get; private set; }

        internal double ImportDurationMs { get; private set; }

        internal double GutDurationMs { get; private set; }

        internal double ReportCopyDurationMs { get; set; }

        internal void SetProcessDuration(string phase, double milliseconds)
        {
            switch (phase)
            {
                case "version":
                    VersionDurationMs = milliseconds;
                    break;
                case "import":
                    ImportDurationMs = milliseconds;
                    break;
                case "gut":
                    GutDurationMs = milliseconds;
                    break;
            }
        }
    }
}
