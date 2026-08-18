using Miller.Core.References;
using Miller.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Indexing;

[Trait("Category", "Scale")]
public sealed class LiveReferenceResolutionScaleTests
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

            var runner = new JulieExtractRunner(binary);
            ExtractReport report = runner.Scan(repo, db, force: true);
            Assert.NotEqual("failed", report.Status);

            IReadOnlyList<IndexedSymbol> symbols = SqliteSymbolReader.Read(db);
            string cSharpParameterTargetId = AssertResolvedCall(
                db,
                symbols,
                targetName: "ExecuteTypedReceiver",
                callerPath: "TypedReceiver.cs",
                language: "csharp");
            string cSharpLocalTargetId = AssertResolvedCall(
                db,
                symbols,
                targetName: "ExecuteTypedLocal",
                callerPath: "TypedReceiver.cs",
                language: "csharp");
            string typeScriptTargetId = AssertResolvedCall(
                db,
                symbols,
                targetName: "executeTypeScriptStatic",
                callerPath: "StaticReceiver.ts",
                language: "typescript");
            string javaScriptTargetId = AssertResolvedCall(
                db,
                symbols,
                targetName: "executeJavaScriptStatic",
                callerPath: "StaticReceiver.js",
                language: "javascript");
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

    private static string AssertResolvedCall(
        string db,
        IReadOnlyList<IndexedSymbol> symbols,
        string targetName,
        string callerPath,
        string language)
    {
        IndexedSymbol target = Assert.Single(
            symbols,
            symbol => symbol.Name == targetName && symbol.Language == language);
        ReferenceEvidenceSet evidence = ReferenceEvidenceReader.Read(
            db,
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
