namespace Miller.Indexing;

/// <summary>
/// Positive test-role facts plus the currency of the file evidence they came from.
/// Role flags never imply runner inventory or extraction completeness.
/// </summary>
public readonly record struct TestRoleEvidence(
    bool IsTest,
    bool IsContainer,
    bool IsLifecycle,
    string Status,
    string? Reason)
{
    public const string CurrentStatus = "current";
    public const string UnknownStatus = "unknown";
    public const string FileStatusReason = "file_status";
    public const string ParseDiagnosticsReason = "parse_diagnostics";
    public const string FileStatusAndParseDiagnosticsReason = "file_status_and_parse_diagnostics";
    public const string FileEvidenceUnavailableReason = "file_evidence_unavailable";

    public bool IsCase => IsTest && !IsLifecycle;

    public static TestRoleEvidence FromArtifactFacts(
        bool isTest,
        bool isContainer,
        bool isLifecycle,
        string? fileStatus,
        bool hasParseDiagnostics,
        bool hasFileEvidence)
    {
        if (!hasFileEvidence)
        {
            return new TestRoleEvidence(
                isTest,
                isContainer,
                isLifecycle,
                UnknownStatus,
                FileEvidenceUnavailableReason);
        }

        bool hasNonIndexedFileStatus = !string.Equals(fileStatus, "indexed", StringComparison.Ordinal);
        string? reason = (hasNonIndexedFileStatus, hasParseDiagnostics) switch
        {
            (false, false) => null,
            (true, false) => FileStatusReason,
            (false, true) => ParseDiagnosticsReason,
            (true, true) => FileStatusAndParseDiagnosticsReason,
        };

        return new TestRoleEvidence(
            isTest,
            isContainer,
            isLifecycle,
            reason is null ? CurrentStatus : UnknownStatus,
            reason);
    }
}
