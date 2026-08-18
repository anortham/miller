using System.Diagnostics;
using Miller.Core.References;
using Miller.Core.Resolution;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Resolution;
using Miller.Indexing.Store;
using Miller.Tests.Indexing.Resolution;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Indexing;

[Trait("Category", "Scale")]
public sealed class LiveReferenceResolutionScaleTests(ITestOutputHelper output)
{
    [Fact]
    public void LiveExtract_TypedAndStaticReceivers_RoundTripThroughReferenceEvidenceReader()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        Assert.Equal(
            $"julie-extract {MillerExtractContract.PinnedJulieExtractVersion}",
            ScaleTestSupport.RunJulie(binary, "--version").Trim());

        string work = Path.Combine(
            Path.GetTempPath(),
            "miller-live-reference-resolution-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string db = Path.Combine(work, ".miller", "symbols.db");
        Directory.CreateDirectory(repo);
        try
        {
            WriteFixtureRepo(repo);

            var runner = new JulieExtractRunner(binary);
            ExtractReport report = runner.Scan(repo, db, force: true);
            Assert.NotEqual("failed", report.Status);

            IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.Read(db);
            string cSharpParameterTargetId = Assert.Single(
                symbols,
                symbol => symbol.Name == "ExecuteTypedReceiver" && symbol.Language == "csharp").SymbolId;
            string cSharpLocalTargetId = Assert.Single(
                symbols,
                symbol => symbol.Name == "ExecuteTypedLocal" && symbol.Language == "csharp").SymbolId;
            string typeScriptTargetId = Assert.Single(
                symbols,
                symbol => symbol.Name == "executeTypeScriptStatic" && symbol.Language == "typescript").SymbolId;
            string javaScriptTargetId = Assert.Single(
                symbols,
                symbol => symbol.Name == "executeJavaScriptStatic" && symbol.Language == "javascript").SymbolId;
            AssertResolutionMethod(db, cSharpParameterTargetId, "tier3_receiver");
            AssertResolutionMethod(db, cSharpLocalTargetId, "tier3_receiver");
            AssertResolutionMethod(db, typeScriptTargetId, "tier3_static_type");
            AssertResolutionMethod(db, javaScriptTargetId, "tier3_static_type");

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = db,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'reference_resolution_version';";
            Assert.Equal("6", Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void StoreExtractAndResolve_QueryTimeMatchesJulieGroundTruth()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        Assert.SkipUnless(
            QueryTimeResolutionParity.BinarySupportsResolve(binary),
            "Pinned julie-extract does not expose store resolve; skip until a resolve-capable pin is restored or Phase 3 retires this gate.");

        string work = Path.Combine(
            Path.GetTempPath(),
            "miller-qtr-parity-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string store = Path.Combine(work, "store");
        Directory.CreateDirectory(repo);
        try
        {
            WriteFixtureRepo(repo);
            ScaleTestSupport.RunJulie(
                binary,
                "store", "import", "--store", store,
                "--family", "11111111-1111-4111-8111-111111111111",
                "--root", repo, "--view", "view-a", "--level", "full", "--jobs", "1", "--json");
            ScaleTestSupport.RunJulie(
                binary,
                "store", "resolve", "--store", store, "--view", "view-a", "--json");

            var binding = new StoreFamilyBinding(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                store,
                "view-a",
                PathCanonicalizer.CanonicalizeRoot(repo),
                StoreBindingState.Ready);
            using FamilyStoreReadSession session = FamilyStoreReadSession.Open(binding);
            QueryTimeResolutionReader reader = session.Resolution;
            RevisionFactCache cache = reader.Cache;
            var resolver = new QueryTimeResolver(cache);
            StoreVisibility visibility = session.Visibility;

            IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.ReadSession(session);
            string cSharpParameterTargetId = AssertResolvedCall(
                session,
                symbols,
                targetName: "ExecuteTypedReceiver",
                callerPath: "TypedReceiver.cs",
                language: "csharp");
            string cSharpLocalTargetId = AssertResolvedCall(
                session,
                symbols,
                targetName: "ExecuteTypedLocal",
                callerPath: "TypedReceiver.cs",
                language: "csharp");
            string typeScriptTargetId = AssertResolvedCall(
                session,
                symbols,
                targetName: "executeTypeScriptStatic",
                callerPath: "StaticReceiver.ts",
                language: "typescript");
            string javaScriptTargetId = AssertResolvedCall(
                session,
                symbols,
                targetName: "executeJavaScriptStatic",
                callerPath: "StaticReceiver.js",
                language: "javascript");

            using SqliteConnection storeRead = QueryTimeResolutionParity.OpenRead(visibility.StoreDatabasePath);
            string? basePath = QueryTimeResolutionParity.LocateResolutionBase(storeRead, visibility);
            Assert.False(string.IsNullOrEmpty(basePath), "Store resolve did not produce a readable resolution base.");
            QueryTimeResolutionParity.AttachResolutionBase(storeRead, basePath!);

            Dictionary<(long VersionId, string Id), StoredResolution> storedIdentifiers =
                QueryTimeResolutionParity.ReadStoredIdentifiers(storeRead, visibility);
            Dictionary<(long VersionId, string Id), StoredResolution> storedPendings =
                QueryTimeResolutionParity.ReadStoredPendings(storeRead, visibility);
            Dictionary<(long VersionId, string Id), QueryTimeResolutionParity.PendingFact> pendings =
                QueryTimeResolutionParity.ReadPendingFacts(storeRead, visibility);
            Dictionary<(long VersionId, string Id), QueryTimeResolutionParity.RelationshipFact> relationships =
                QueryTimeResolutionParity.ReadRelationshipFacts(storeRead, visibility);

            ParityReport identifiers = QueryTimeResolutionParity.CompareIdentifiers(
                storeRead, visibility, cache, resolver, storedIdentifiers, pendings, relationships);
            ParityReport pendingRows = QueryTimeResolutionParity.ComparePendings(
                cache, resolver, storedPendings, pendings);
            output.WriteLine(
                "identifiers compared={0} matched={1} under_resolved={2} divergences={3}",
                identifiers.Compared,
                identifiers.Matched,
                identifiers.UnderResolved,
                identifiers.Divergences.Count);
            output.WriteLine(
                "pendings compared={0} matched={1} under_resolved={2} divergences={3}",
                pendingRows.Compared,
                pendingRows.Matched,
                pendingRows.UnderResolved,
                pendingRows.Divergences.Count);
            foreach (string row in identifiers.UnderResolvedSamples)
                output.WriteLine("under_resolved " + row);
            foreach (string row in pendingRows.UnderResolvedSamples)
                output.WriteLine("under_resolved " + row);
            Assert.True(identifiers.Passed, string.Join(Environment.NewLine, identifiers.Divergences));
            Assert.True(pendingRows.Passed, string.Join(Environment.NewLine, pendingRows.Divergences));
            Assert.True(identifiers.Compared > 0);

            string[] candidateIds = [cSharpParameterTargetId, cSharpLocalTargetId, typeScriptTargetId, javaScriptTargetId];
            string[] graph = QueryTimeResolutionParity.SerializeGraph(reader, storeRead, candidateIds);
            string[] expectedGraph = QueryTimeResolutionParity.ReconstructGraphFromStore(
                storeRead, visibility, cache, candidateIds, storedIdentifiers, storedPendings, pendings, relationships);
            Assert.Equal(expectedGraph, graph);

            string[] evidence = QueryTimeResolutionParity.SerializeEvidence(reader, storeRead, candidateIds);
            string[] export = QueryTimeResolutionParity.SerializeExport(reader, storeRead);
            Assert.NotEmpty(evidence);
            Assert.NotEmpty(export);
            Assert.Equal(evidence, evidence.Distinct(StringComparer.Ordinal).OrderBy(static row => row, StringComparer.Ordinal));
            Assert.Equal(export, export.Distinct(StringComparer.Ordinal).OrderBy(static row => row, StringComparer.Ordinal));
            Assert.Contains(evidence, row => row.Contains("identifier_resolution", StringComparison.Ordinal));
            Assert.Contains(export, row => row.Contains("identifier_resolution", StringComparison.Ordinal));

            storeRead.Dispose();
            session.Dispose();
            File.AppendAllText(Path.Combine(repo, "TypedReceiver.cs"), Environment.NewLine + "// save-to-answer");
            long saveStarted = Stopwatch.GetTimestamp();
            ScaleTestSupport.RunJulie(
                binary,
                "store", "update", "--store", store, "--root", repo, "--view", "view-a",
                "--file", "TypedReceiver.cs", "--level", "full", "--jobs", "1", "--json");
            using FamilyStoreReadSession after = FamilyStoreReadSession.Open(binding);
            ReferenceEvidenceSet afterEvidence = ReferenceEvidenceReader.Read(
                after,
                cSharpParameterTargetId,
                new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 0));
            Assert.Contains(afterEvidence.Exact, row => row.TargetSymbolId == cSharpParameterTargetId);
            TimeSpan saveToAnswer = Stopwatch.GetElapsedTime(saveStarted);
            output.WriteLine("save_to_answer_ms=" + QueryTimeResolutionParity.FmtMs(saveToAnswer));
            Assert.True(
                saveToAnswer.TotalSeconds <= 5,
                "Save-to-correct-answer exceeded 5 s: " + saveToAnswer.TotalSeconds.ToString("0.000"));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    internal static void WriteFixtureRepo(string repo)
    {
        File.WriteAllText(Path.Combine(repo, "TypedReceiver.cs"), """
            namespace ReferenceResolution;

            public sealed class CSharpWorker
            {
                public void ExecuteTypedReceiver()
                {
                }

                public void ExecuteTypedLocal()
                {
                }
            }

            public sealed class CSharpCaller
            {
                public void Invoke(CSharpWorker worker)
                {
                    worker.ExecuteTypedReceiver();
                }

                public void InvokeLocal()
                {
                    CSharpWorker worker = new CSharpWorker();
                    worker.ExecuteTypedLocal();
                }
            }
            """);
        File.WriteAllText(Path.Combine(repo, "StaticReceiver.ts"), """
            export class TypeScriptWorker {
              static executeTypeScriptStatic(): void {
              }
            }

            export function invokeTypeScript(): void {
              TypeScriptWorker.executeTypeScriptStatic();
            }
            """);
        File.WriteAllText(Path.Combine(repo, "StaticReceiver.js"), """
            export class JavaScriptWorker {
              static executeJavaScriptStatic() {
              }
            }

            export function invokeJavaScript() {
              JavaScriptWorker.executeJavaScriptStatic();
            }
            """);
    }

    private static string AssertResolvedCall(
        IWorkspaceReadSession session,
        IReadOnlyList<IndexedSymbol> symbols,
        string targetName,
        string callerPath,
        string language)
    {
        IndexedSymbol target = Assert.Single(
            symbols,
            symbol => symbol.Name == targetName && symbol.Language == language);
        ReferenceEvidenceSet evidence = ReferenceEvidenceReader.Read(
            session,
            target.SymbolId,
            new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 0));

        ReferenceEvidence call = Assert.Single(
            evidence.Exact,
            row => row.Kind == ReferenceKind.Call && row.FilePath == callerPath);
        Assert.Equal(target.SymbolId, call.TargetSymbolId);
        Assert.Equal(language, call.Language);
        Assert.Equal(ReferenceResolutionStatus.Exact, call.ResolutionStatus);
        Assert.True(call.IsExact);
        Assert.Equal(ReferenceEvidenceSource.IdentifierResolution, call.Source);
        Assert.NotNull(call.ResolutionTier);
        Assert.Equal("target_token", call.SiteProvenance);
        return target.SymbolId;
    }

    private static void AssertResolutionMethod(string db, string targetSymbolId, string expectedMethod)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT method
            FROM identifier_resolutions
            WHERE target_symbol_id = $target
              AND outcome = 'resolved';
            """;
        command.Parameters.AddWithValue("$target", targetSymbolId);
        Assert.Equal(expectedMethod, command.ExecuteScalar()?.ToString());
    }
}
