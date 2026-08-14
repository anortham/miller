using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

[Trait("Category", "Scale")]
public sealed class ReferenceEvidenceReaderTask7BScaleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void ReadManyFamilyStore_EmitsTask7BEvidence()
    {
        string? storeRoot = Environment.GetEnvironmentVariable("MILLER_TASK7B_STORE_ROOT");
        string? outputPath = Environment.GetEnvironmentVariable("MILLER_TASK7B_OUTPUT");
        string? familyId = Environment.GetEnvironmentVariable("MILLER_TASK7B_FAMILY_ID");
        string? viewId = Environment.GetEnvironmentVariable("MILLER_TASK7B_VIEW_ID");
        string? workspaceRoot = Environment.GetEnvironmentVariable("MILLER_TASK7B_WORKSPACE_ROOT");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(storeRoot) ||
            string.IsNullOrWhiteSpace(outputPath) ||
            string.IsNullOrWhiteSpace(familyId) ||
            string.IsNullOrWhiteSpace(viewId) ||
            string.IsNullOrWhiteSpace(workspaceRoot),
            "Set the MILLER_TASK7B_* environment variables to run the Task 7B family-store evidence harness.");
        Assert.True(Guid.TryParse(familyId, out Guid parsedFamilyId));

        var binding = new StoreFamilyBinding(
            parsedFamilyId,
            Path.GetFullPath(storeRoot!),
            viewId!,
            Path.GetFullPath(workspaceRoot!),
            StoreBindingState.Ready);
        string[] reverseIds;
        string[] forwardIds;
        using (FamilyStoreReadSession candidateSession = FamilyStoreReadSession.Open(binding))
        {
            reverseIds = candidateSession.Read(ReadVisibleExactResolutionTargets);
            forwardIds = candidateSession.Read(ReadVisibleContainingSymbols);
        }
        Assert.SkipUnless(reverseIds.Length >= 100, "The store has fewer than 100 visible exact resolution targets.");
        Assert.SkipUnless(forwardIds.Length >= 100, "The store has fewer than 100 visible containing symbols.");

        string fullOutputPath = Path.GetFullPath(outputPath!);
        string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        using var output = new StreamWriter(fullOutputPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        ReferenceEvidenceQuery query = new(new ReferenceEvidenceBounds(ExactLimit: 50, FallbackLimit: 50));
        foreach ((string direction, string[] candidates) in new[]
        {
            ("reverse", reverseIds),
            ("forward", forwardIds),
        })
        {
            foreach (int size in new[] { 1, 100 })
            {
                using FamilyStoreReadSession session = FamilyStoreReadSession.Open(binding);
                string[] selected = candidates.Take(size).ToArray();
                Measure(output, session, direction, size, selected, "cold", 0, query, emittedKeys);
                Measure(output, session, direction, size, selected, "warmup", 0, query, emittedKeys);
                for (int run = 1; run <= 3; run++)
                {
                    Measure(output, session, direction, size, selected, $"warm-{run}", run, query, emittedKeys);
                }
            }
        }

        Assert.Equal(40, emittedKeys.Count);
    }

    private static void Measure(
        TextWriter output,
        FamilyStoreReadSession session,
        string direction,
        int size,
        IReadOnlyList<string> candidates,
        string cacheState,
        int run,
        ReferenceEvidenceQuery query,
        ISet<string> emittedKeys)
    {
        var observations = new List<ReferenceEvidenceObservation>();
        IReadOnlyDictionary<string, ReferenceEvidenceBundle> observed = ReferenceEvidenceReader.ReadManyObserved(
            session,
            candidates,
            query,
            new ReferenceEvidenceObservationOptions(observations.Add, CaptureQueryPlan: true));
        IReadOnlyDictionary<string, ReferenceEvidenceBundle> publicResult = ReferenceEvidenceReader.ReadMany(
            session,
            candidates,
            query);
        string observedSerialized = SerializeResult(observed);
        Assert.Equal(observedSerialized, SerializeResult(publicResult));
        Assert.Equal(5, observations.Count);

        foreach (Task7BArm arm in Task7BArmMapping.ForDirection(direction))
        {
            ReferenceEvidenceObservation observation = Assert.Single(
                observations,
                candidate => candidate.Phase == arm.Phase);
            string key = $"{direction}|{arm.Name}|{size}|{cacheState}|{run}";
            Assert.True(emittedKeys.Add(key), $"Duplicate Task 7B output key '{key}'.");
            var row = new
            {
                Direction = direction,
                Arm = arm.Name,
                Size = size,
                CacheState = cacheState,
                Run = run,
                Plans = observation.QueryPlan,
                RequestedCandidates = observation.RequestedCandidateCount,
                RawRows = observation.ReturnedRawRowCount,
                ReturnedBundleCount = observed.Count,
                ReturnedEvidenceCount = ReturnedEvidenceCount(observed, arm.Phase),
                StatementCount = observations.Count,
                ElapsedMs = observation.ElapsedMilliseconds,
                ResultDigest = Digest(observedSerialized),
            };
            output.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
        }
    }

    private static int ReturnedEvidenceCount(
        IReadOnlyDictionary<string, ReferenceEvidenceBundle> result,
        ReferenceEvidenceReadPhase phase) => phase switch
    {
        ReferenceEvidenceReadPhase.InboundExact => result.Values.Sum(bundle => bundle.Inbound.Exact.Count),
        ReferenceEvidenceReadPhase.InboundFallback => result.Values.Sum(bundle => bundle.Inbound.Fallback.Count),
        ReferenceEvidenceReadPhase.OutgoingExact => result.Values.Sum(bundle => bundle.Outgoing.Exact.Count),
        ReferenceEvidenceReadPhase.OutgoingFallback => result.Values.Sum(bundle => bundle.Outgoing.Fallback.Count),
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unsupported Task 7B evidence arm."),
    };

    private static string SerializeResult(IReadOnlyDictionary<string, ReferenceEvidenceBundle> result) =>
        JsonSerializer.Serialize(
            result.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            JsonOptions);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string[] ReadVisibleExactResolutionTargets(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT target.symbol_id
            FROM main.symbols AS target
            JOIN _miller_visible_entries AS e ON e.version_id=target.version_id
            JOIN resolution_base.identifier_resolutions AS resolution
              ON resolution.target_version_id=target.version_id
             AND resolution.target_symbol_id=target.symbol_id
            WHERE resolution.target_symbol_id IS NOT NULL
            ORDER BY target.symbol_id
            LIMIT 100;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids.ToArray();
    }

    private static string[] ReadVisibleContainingSymbols(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT containing.symbol_id
            FROM main.symbols AS containing
            JOIN _miller_visible_entries AS e ON e.version_id=containing.version_id
            JOIN main.identifiers AS identifier
              ON identifier.version_id=containing.version_id
             AND identifier.containing_symbol_id=containing.symbol_id
            WHERE identifier.containing_symbol_id IS NOT NULL
            ORDER BY containing.symbol_id
            LIMIT 100;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids.ToArray();
    }
}
