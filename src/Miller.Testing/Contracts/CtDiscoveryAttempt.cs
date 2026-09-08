using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miller.Testing;

public enum CtDiscoveryStage
{
    PrerequisiteCheck,
    FrameworkClassification,
    ProcessSpawn,
    Execution,
    Parsing,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CtDiscoveryOutcome
{
    Succeeded,
    Failed,
    Refused,
    TimedOut,
}

public sealed record CtDiscoveryAttempt(
    string AttemptId,
    string WorkspaceId,
    string ProjectPath,
    string Framework,
    string ProviderSource,
    string IndexIdentity,
    long Revision,
    CtDiscoveryStage Stage,
    CtDiscoveryOutcome Outcome,
    DateTimeOffset AttemptedAtUtc,
    string? FailureReason,
    string? FailureDetail,
    string? Remedy,
    int? ExitCode = null,
    TestProcessCommand? Command = null,
    string? StandardOutput = null,
    string? StandardError = null,
    string? ExceptionType = null,
    string? ArtifactPath = null);

public sealed record CtDiscoveryAttemptSummary(
    string ProjectPath,
    string LatestAttemptId,
    string Framework,
    CtDiscoveryOutcome Outcome,
    CtDiscoveryStage Stage,
    string IndexIdentity,
    long Revision,
    DateTimeOffset AttemptedAtUtc,
    string? FailureReason,
    string? Remedy,
    string ArtifactPath);

public sealed record CtDiscoveryWorkspaceLedger(
    string WorkspaceId,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyDictionary<string, CtDiscoveryAttemptSummary> Projects);

public interface ICtDiscoveryLedger
{
    CtDiscoveryAttemptSummary? GetLatestAttempt(string projectPath);
    CtDiscoveryAttemptSummary? GetLatestAttempt(string workspaceRoot, string projectPath);
    void RecordAttempt(string workspaceRoot, CtDiscoveryAttempt attempt);
    void ClearAttempt(string workspaceRoot, string projectPath);
}

public sealed class CtDiscoveryLedger : ICtDiscoveryLedger
{
    private const int MaxOutputBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, CtDiscoveryAttemptSummary> _cachedSummaries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _projectRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _workspaceIds = new(StringComparer.Ordinal);

    public CtDiscoveryAttemptSummary? GetLatestAttempt(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        lock (_gate)
        {
            return _cachedSummaries.TryGetValue(projectPath, out CtDiscoveryAttemptSummary? summary)
                ? summary
                : null;
        }
    }

    public CtDiscoveryAttemptSummary? GetLatestAttempt(string workspaceRoot, string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        EnsureLoaded(workspaceRoot);
        lock (_gate)
        {
            return _cachedSummaries.TryGetValue(projectPath, out CtDiscoveryAttemptSummary? summary)
                ? summary
                : null;
        }
    }

    public void RecordAttempt(string workspaceRoot, CtDiscoveryAttempt attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(attempt);

        string attemptsDir = Path.Combine(workspaceRoot, ".miller", "ct", "discovery-attempts");
        Directory.CreateDirectory(attemptsDir);

        string artifactPath = attempt.ArtifactPath
            ?? Path.Combine(attemptsDir, $"{attempt.AttemptId}.json");

        // Bound stdout and stderr to 32KB
        string? boundedStdout = BoundOutput(attempt.StandardOutput, MaxOutputBytes);
        string? boundedStderr = BoundOutput(attempt.StandardError, MaxOutputBytes);

        CtDiscoveryAttempt boundedAttempt = attempt with
        {
            ArtifactPath = artifactPath,
            StandardOutput = boundedStdout,
            StandardError = boundedStderr,
        };

        // Write attempt artifact JSON
        string artifactJson = JsonSerializer.Serialize(boundedAttempt, JsonOptions);
        WriteAtomic(artifactPath, artifactJson);

        var summary = new CtDiscoveryAttemptSummary(
            ProjectPath: boundedAttempt.ProjectPath,
            LatestAttemptId: boundedAttempt.AttemptId,
            Framework: boundedAttempt.Framework,
            Outcome: boundedAttempt.Outcome,
            Stage: boundedAttempt.Stage,
            IndexIdentity: boundedAttempt.IndexIdentity,
            Revision: boundedAttempt.Revision,
            AttemptedAtUtc: boundedAttempt.AttemptedAtUtc,
            FailureReason: boundedAttempt.FailureReason,
            Remedy: boundedAttempt.Remedy,
            ArtifactPath: artifactPath);

        lock (_gate)
        {
            EnsureLoadedLocked(workspaceRoot);
            _cachedSummaries[boundedAttempt.ProjectPath] = summary;
            _projectRoots[boundedAttempt.ProjectPath] = Path.GetFullPath(workspaceRoot);
            _workspaceIds[Path.GetFullPath(workspaceRoot)] = boundedAttempt.WorkspaceId;
            PersistLedgerLocked(workspaceRoot, boundedAttempt.WorkspaceId);
        }
    }

    public void ClearAttempt(string workspaceRoot, string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        lock (_gate)
        {
            EnsureLoadedLocked(workspaceRoot);
            if (_cachedSummaries.Remove(projectPath))
            {
                _projectRoots.Remove(projectPath);
                PersistLedgerLocked(workspaceRoot, _workspaceIds.GetValueOrDefault(Path.GetFullPath(workspaceRoot), ""));
            }
        }
    }

    public static CtDiscoveryAttempt? LoadAttempt(string artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath))
            return null;

        try
        {
            string json = File.ReadAllText(artifactPath);
            return JsonSerializer.Deserialize<CtDiscoveryAttempt>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static CtDiscoveryWorkspaceLedger? LoadLedger(string workspaceRoot)
    {
        string ledgerPath = Path.Combine(workspaceRoot, ".miller", "ct-discovery.json");
        if (!File.Exists(ledgerPath))
            return null;

        try
        {
            string json = File.ReadAllText(ledgerPath);
            return JsonSerializer.Deserialize<CtDiscoveryWorkspaceLedger>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void EnsureLoaded(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        lock (_gate)
        {
            EnsureLoadedLocked(workspaceRoot);
        }
    }

    private void EnsureLoadedLocked(string workspaceRoot)
    {
        if (!_loadedRoots.Add(workspaceRoot))
            return;

        CtDiscoveryWorkspaceLedger? ledger = LoadLedger(workspaceRoot);
        if (ledger?.Projects is { } projects)
        {
            _workspaceIds[Path.GetFullPath(workspaceRoot)] = ledger.WorkspaceId;
            foreach ((string projectPath, CtDiscoveryAttemptSummary summary) in projects)
            {
                _cachedSummaries[projectPath] = summary;
                _projectRoots[projectPath] = Path.GetFullPath(workspaceRoot);
            }
        }
    }

    private void PersistLedgerLocked(string workspaceRoot, string workspaceId)
    {
        string dotMiller = Path.Combine(workspaceRoot, ".miller");
        Directory.CreateDirectory(dotMiller);

        string ledgerPath = Path.Combine(dotMiller, "ct-discovery.json");
        var ledger = new CtDiscoveryWorkspaceLedger(
            WorkspaceId: workspaceId,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            Projects: _cachedSummaries.Where(row => _projectRoots.GetValueOrDefault(row.Key) == Path.GetFullPath(workspaceRoot))
                .ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal));

        string json = JsonSerializer.Serialize(ledger, JsonOptions);
        WriteAtomic(ledgerPath, json);
    }

    private static void WriteAtomic(string destinationPath, string content)
    {
        string directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $".tmp-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, destinationPath, overwrite: true);
    }

    private static string? BoundOutput(string? output, int maxBytes)
    {
        if (output is null)
            return null;

        if (Encoding.UTF8.GetByteCount(output) <= maxBytes)
            return output;

        var bounded = new StringBuilder();
        int bytes = Encoding.UTF8.GetByteCount("…");
        foreach (Rune rune in output.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes)
                break;
            bounded.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return bounded.Append('…').ToString();
    }
}
