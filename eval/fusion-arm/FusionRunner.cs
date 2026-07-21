using System.Text;

namespace FusionArm;

/// <summary>The resolved inputs for one arm run.</summary>
public sealed record FusionRunOptions(
    string QueriesPath,
    string LexicalDir,
    string SemanticDir,
    string OutPath,
    FusionConfig Config);

/// <summary>What a run produced: how many query-set rows were seen, how many emitted a results row, and which were
/// skipped for a missing input file.</summary>
public sealed record FusionRunSummary(
    int QueryCount,
    int EmittedCount,
    int MissingCount,
    IReadOnlyList<string> MissingQueryIds);

/// <summary>Drives the fused arm over a query set: loads each query's per-query arm files, routes + fuses through
/// <see cref="Fuser"/>, and writes the retrieval-eval results JSONL. A query whose required input file is absent
/// emits no row and is counted, never thrown.</summary>
public static class FusionRunner
{
    public static FusionRunSummary Run(FusionRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<QueryRow> queries = Json.ReadQuerySet(options.QueriesPath);
        var rows = new List<FusedResultRow>(queries.Count);
        var missing = new List<string>();

        foreach (QueryRow query in queries)
        {
            string lexicalPath = Path.Combine(options.LexicalDir, query.QueryId + ".json");
            if (!File.Exists(lexicalPath))
            {
                missing.Add(query.QueryId);
                continue;
            }

            IReadOnlyList<ArmInputRow> lexical = Json.ReadArmFile(lexicalPath);
            FusionPlan plan = Fuser.Plan(query.Query, lexical, options.Config);

            IReadOnlyList<ArmInputRow> semantic = [];
            if (plan.Mode == FusionMode.Fuse)
            {
                string semanticPath = Path.Combine(options.SemanticDir, query.QueryId + ".json");
                if (!File.Exists(semanticPath))
                {
                    missing.Add(query.QueryId);
                    continue;
                }

                semantic = Json.ReadArmFile(semanticPath);
            }

            IReadOnlyList<string> ranked = Fuser.Apply(plan, lexical, semantic, options.Config);
            rows.Add(new FusedResultRow { QueryId = query.QueryId, Ranked = ranked });
        }

        WriteResults(options.OutPath, rows);
        return new FusionRunSummary(queries.Count, rows.Count, missing.Count, missing);
    }

    static void WriteResults(string outPath, IReadOnlyList<FusedResultRow> rows)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var builder = new StringBuilder();
        foreach (FusedResultRow row in rows)
            builder.Append(Json.SerializeRow(row)).Append('\n');

        File.WriteAllText(outPath, builder.ToString());
    }
}
