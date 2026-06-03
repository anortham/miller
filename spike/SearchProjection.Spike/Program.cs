using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Core.Tokenization;
using Miller.Indexing;

namespace SearchProjection.Spike;

internal static class Program
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            string dbPath = Path.GetFullPath(options.DbPath);
            if (!File.Exists(dbPath))
            {
                Console.Error.WriteLine($"DB not found: {dbPath}");
                return 2;
            }

            var shape = DetectShape(dbPath, options);
            var dbFacts = LoadDbFacts(dbPath, shape);
            PrintHeader(dbPath, dbFacts, options);

            MeasureFullLoad(dbPath, options, shape);
            MeasureSymbolProjection(dbPath, options, shape, wide: false);
            MeasureSymbolProjection(dbPath, options, shape, wide: true);

            var corpusBuild = MeasureBuild(() => LoadContentCorpus(dbPath, dbFacts.RootPath, options, shape), out ContentCorpus corpus);
            PrintBuild("content-corpus read/hash/decode", corpusBuild, new[]
            {
                $"{corpus.Documents.Count:N0} docs",
                $"{FormatBytes(corpus.IndexedBytes)} indexed bytes",
                $"{corpus.ManifestRows:N0} manifest rows",
                $"{corpus.SkippedSummary}"
            });

            if (corpus.Documents.Count == 0)
            {
                Console.WriteLine("content indexes skipped: no content documents survived the filters.");
                return 0;
            }

            MeasureProjectionIndex("content-inmemory", corpus.Documents, options);

            if (!options.NoFts)
            {
                MeasureFtsIndex("content-fts5", corpus.Documents, options, trigram: false);
                MeasureFtsIndex("content-fts5-trigram", corpus.Documents, options, trigram: true);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void MeasureFullLoad(string dbPath, Options options, DbShape shape)
    {
        if (shape.Kind != DbShapeKind.V1)
        {
            Console.WriteLine("full RepositoryIndexLoader.Load");
            Console.WriteLine("  skipped: legacy Julie DBs are useful for raw projection scale measurements, but the production Miller loader only supports julie-extractors v1.");
            Console.WriteLine();
            return;
        }

        var build = MeasureBuild(() => RepositoryIndexLoader.Load(dbPath), out MillerRepositoryIndex index);
        var query = MeasureQueries(options, q => index.Search(q, options.Limit).Count);

        PrintBuild("full RepositoryIndexLoader.Load", build, new[]
        {
            $"query p50 {query.P50Ms:F3} ms",
            $"p95 {query.P95Ms:F3} ms",
            $"{query.FirstPassHits:N0} top{options.Limit}-hits/{options.Queries.Length}q"
        });

        GC.KeepAlive(index);
    }

    private static void MeasureSymbolProjection(string dbPath, Options options, DbShape shape, bool wide)
    {
        string name = wide ? "symbol-wide inmemory" : "symbol-current inmemory";
        var build = MeasureBuild(() => BuildSymbolProjection(dbPath, options, shape, wide), out ProjectionIndex index);
        var query = MeasureQueries(options, q => index.Search(q, options.Limit));

        PrintBuild(name, build, new[]
        {
            $"{index.DocumentCount:N0} docs",
            $"{index.TermCount:N0} terms",
            $"{index.PostingCount:N0} postings",
            $"~{FormatBytes(index.EstimatedPostingBytes)} postings payload",
            $"query p50 {query.P50Ms:F3} ms",
            $"p95 {query.P95Ms:F3} ms",
            $"{query.FirstPassHits:N0} top{options.Limit}-hits/{options.Queries.Length}q"
        });

        GC.KeepAlive(index);
    }

    private static void MeasureProjectionIndex(string name, IReadOnlyList<ProjectionDocument> documents, Options options)
    {
        var build = MeasureBuild(() => ProjectionIndex.Build(documents), out ProjectionIndex index);
        var query = MeasureQueries(options, q => index.Search(q, options.Limit));

        PrintBuild(name, build, new[]
        {
            $"{index.DocumentCount:N0} docs",
            $"{index.TermCount:N0} terms",
            $"{index.PostingCount:N0} postings",
            $"~{FormatBytes(index.EstimatedPostingBytes)} postings payload",
            $"query p50 {query.P50Ms:F3} ms",
            $"p95 {query.P95Ms:F3} ms",
            $"{query.FirstPassHits:N0} top{options.Limit}-hits/{options.Queries.Length}q"
        });

        GC.KeepAlive(index);
    }

    private static void MeasureFtsIndex(
        string name,
        IReadOnlyList<ProjectionDocument> documents,
        Options options,
        bool trigram)
    {
        var build = MeasureBuild(() => FtsIndex.Build(documents, options, trigram), out FtsIndex index);
        using (index)
        {
            var query = MeasureQueries(options, q => index.Search(q, options.Limit));
            PrintBuild(name, build, new[]
            {
                $"{documents.Count:N0} docs",
                $"sqlite {FormatBytes(index.FileBytes)}",
                $"query p50 {query.P50Ms:F3} ms",
                $"p95 {query.P95Ms:F3} ms",
                $"{query.FirstPassHits:N0} top{options.Limit}-hits/{options.Queries.Length}q",
                options.KeepFts ? index.DbPath : "temp file deleted"
            });
        }
    }

    private static ProjectionIndex BuildSymbolProjection(string dbPath, Options options, DbShape shape, bool wide)
    {
        using var connection = OpenReadOnly(dbPath);
        var documents = wide
            ? LoadWideSymbolDocuments(connection, shape, options.SymbolExtraCharLimit)
            : LoadCurrentSymbolDocuments(connection, shape);
        return ProjectionIndex.Build(documents);
    }

    private static IReadOnlyList<ProjectionDocument> LoadCurrentSymbolDocuments(SqliteConnection connection, DbShape shape)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT name, signature, {shape.SymbolPathColumn} AS path
            FROM symbols
            WHERE name IS NOT NULL
            ORDER BY {shape.SymbolPathColumn}, start_line, {shape.SymbolIdColumn};
            """;

        var documents = new List<ProjectionDocument>();
        using var reader = command.ExecuteReader();
        int nameOrdinal = reader.GetOrdinal("name");
        int signatureOrdinal = reader.GetOrdinal("signature");
        int pathOrdinal = reader.GetOrdinal("path");

        while (reader.Read())
        {
            string name = reader.GetString(nameOrdinal);
            string signature = reader.IsDBNull(signatureOrdinal) ? string.Empty : reader.GetString(signatureOrdinal);
            string path = reader.GetString(pathOrdinal);
            string text = signature.Length == 0 ? name : name + ' ' + signature;
            documents.Add(new ProjectionDocument(documents.Count, path + "#" + name, path, text));
        }

        return documents;
    }

    private static IReadOnlyList<ProjectionDocument> LoadWideSymbolDocuments(SqliteConnection connection, DbShape shape, int extraCharLimit)
    {
        var buildersById = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var remainingExtraCharsById = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = new List<(string SymbolId, string Label, string Path)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {shape.SymbolIdColumn} AS symbol_id, name, signature, doc_comment, {shape.SymbolPathColumn} AS path
                FROM symbols
                WHERE name IS NOT NULL
                ORDER BY {shape.SymbolPathColumn}, start_line, {shape.SymbolIdColumn};
                """;

            using var reader = command.ExecuteReader();
            int idOrdinal = reader.GetOrdinal("symbol_id");
            int nameOrdinal = reader.GetOrdinal("name");
            int signatureOrdinal = reader.GetOrdinal("signature");
            int docCommentOrdinal = reader.GetOrdinal("doc_comment");
            int pathOrdinal = reader.GetOrdinal("path");

            while (reader.Read())
            {
                string id = reader.GetString(idOrdinal);
                string name = reader.GetString(nameOrdinal);
                string signature = reader.IsDBNull(signatureOrdinal) ? string.Empty : reader.GetString(signatureOrdinal);
                string docComment = reader.IsDBNull(docCommentOrdinal) ? string.Empty : reader.GetString(docCommentOrdinal);
                string path = reader.GetString(pathOrdinal);

                var builder = new StringBuilder(name.Length + signature.Length + docComment.Length + path.Length + 16);
                AppendText(builder, name);
                AppendText(builder, signature);
                AppendText(builder, docComment);
                AppendText(builder, path);

                buildersById[id] = builder;
                remainingExtraCharsById[id] = extraCharLimit;
                ordered.Add((id, path + "#" + name, path));
            }
        }

        AppendIdentifierFacts(connection, shape, buildersById, remainingExtraCharsById);
        AppendLiteralFacts(connection, shape, buildersById, remainingExtraCharsById);

        var documents = new List<ProjectionDocument>(ordered.Count);
        foreach (var symbol in ordered)
        {
            documents.Add(new ProjectionDocument(
                documents.Count,
                symbol.Label,
                symbol.Path,
                buildersById[symbol.SymbolId].ToString()));
        }

        return documents;
    }

    private static void AppendIdentifierFacts(
        SqliteConnection connection,
        DbShape shape,
        Dictionary<string, StringBuilder> buildersById,
        Dictionary<string, int> remainingExtraCharsById)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT containing_symbol_id, name, code_context
            FROM identifiers
            WHERE containing_symbol_id IS NOT NULL
            ORDER BY {shape.IdentifierPathColumn}, start_line, {shape.IdentifierIdColumn};
            """;

        using var reader = command.ExecuteReader();
        int idOrdinal = reader.GetOrdinal("containing_symbol_id");
        int nameOrdinal = reader.GetOrdinal("name");
        int contextOrdinal = reader.GetOrdinal("code_context");

        while (reader.Read())
        {
            string id = reader.GetString(idOrdinal);
            if (!buildersById.TryGetValue(id, out var builder))
                continue;

            AppendExtra(builder, remainingExtraCharsById, id, reader.GetString(nameOrdinal));
            if (!reader.IsDBNull(contextOrdinal))
                AppendExtra(builder, remainingExtraCharsById, id, reader.GetString(contextOrdinal));
        }
    }

    private static void AppendLiteralFacts(
        SqliteConnection connection,
        DbShape shape,
        Dictionary<string, StringBuilder> buildersById,
        Dictionary<string, int> remainingExtraCharsById)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT containing_symbol_id, literal_text
            FROM literals
            WHERE containing_symbol_id IS NOT NULL
            ORDER BY {shape.LiteralPathColumn}, start_line, {shape.LiteralIdColumn};
            """;

        using var reader = command.ExecuteReader();
        int idOrdinal = reader.GetOrdinal("containing_symbol_id");
        int textOrdinal = reader.GetOrdinal("literal_text");

        while (reader.Read())
        {
            string id = reader.GetString(idOrdinal);
            if (!buildersById.TryGetValue(id, out var builder))
                continue;

            AppendExtra(builder, remainingExtraCharsById, id, reader.GetString(textOrdinal));
        }
    }

    private static ContentCorpus LoadContentCorpus(string dbPath, string rootPath, Options options, DbShape shape)
    {
        if (shape.Kind == DbShapeKind.V1 && string.IsNullOrWhiteSpace(rootPath))
            throw new InvalidOperationException("artifact_metadata.root_path is missing; cannot re-source file content from disk.");

        using var connection = OpenReadOnly(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = shape.Kind == DbShapeKind.V1
            ? """
            SELECT path, language, content_hash, content_bytes, status, NULL AS content
            FROM files
            ORDER BY path;
            """
            : """
            SELECT path, language, hash AS content_hash, size AS content_bytes, 'indexed' AS status, content
            FROM files
            ORDER BY path;
            """;

        using var reader = command.ExecuteReader();
        int pathOrdinal = reader.GetOrdinal("path");
        int languageOrdinal = reader.GetOrdinal("language");
        int hashOrdinal = reader.GetOrdinal("content_hash");
        int bytesOrdinal = reader.GetOrdinal("content_bytes");
        int statusOrdinal = reader.GetOrdinal("status");
        int contentOrdinal = reader.GetOrdinal("content");

        var documents = new List<ProjectionDocument>();
        int manifestRows = 0;
        int scopeSkipped = 0;
        int statusSkipped = 0;
        int tooLargeSkipped = 0;
        int missingSkipped = 0;
        int hashMismatchSkipped = 0;
        int nonUtf8Skipped = 0;
        int ioSkipped = 0;
        long indexedBytes = 0;

        while (reader.Read())
        {
            manifestRows++;
            string path = reader.GetString(pathOrdinal);
            string language = reader.GetString(languageOrdinal);
            string contentHash = reader.GetString(hashOrdinal);
            long contentBytes = reader.GetInt64(bytesOrdinal);
            string status = reader.GetString(statusOrdinal);

            if (!string.Equals(status, "indexed", StringComparison.Ordinal))
            {
                statusSkipped++;
                continue;
            }

            if (options.ContentScope == ContentScope.Docs && !IsDocsLike(path, language))
            {
                scopeSkipped++;
                continue;
            }

            if (contentBytes > options.ContentMaxBytes)
            {
                tooLargeSkipped++;
                continue;
            }

            string text;
            int byteCount;
            if (shape.Kind == DbShapeKind.Legacy)
            {
                if (reader.IsDBNull(contentOrdinal))
                {
                    missingSkipped++;
                    continue;
                }

                text = reader.GetString(contentOrdinal);
                byteCount = StrictUtf8.GetByteCount(text);
            }
            else
            {
                string fullPath = Path.Combine(rootPath, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                {
                    missingSkipped++;
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(fullPath);
                }
                catch (IOException)
                {
                    ioSkipped++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    ioSkipped++;
                    continue;
                }

                if (options.HashVerify)
                {
                    string actual = ContentHasher.Blake3Hex(bytes);
                    string expected = ContentHasher.NormalizeHash(contentHash);
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        hashMismatchSkipped++;
                        continue;
                    }
                }

                try
                {
                    text = StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    nonUtf8Skipped++;
                    continue;
                }

                byteCount = bytes.Length;
            }

            documents.Add(new ProjectionDocument(documents.Count, path, path, text));
            indexedBytes += byteCount;
        }

        return new ContentCorpus(
            documents,
            manifestRows,
            indexedBytes,
            statusSkipped,
            scopeSkipped,
            tooLargeSkipped,
            missingSkipped,
            hashMismatchSkipped,
            nonUtf8Skipped,
            ioSkipped);
    }

    private static DbShape DetectShape(string dbPath, Options options)
    {
        using var connection = OpenReadOnly(dbPath);
        if (HasTable(connection, "artifact_metadata"))
        {
            return new DbShape(
                DbShapeKind.V1,
                SymbolIdColumn: "symbol_id",
                SymbolPathColumn: "path",
                IdentifierIdColumn: "identifier_id",
                IdentifierPathColumn: "path",
                LiteralIdColumn: "literal_id",
                LiteralPathColumn: "path");
        }

        if (HasColumn(connection, "symbols", "id")
            && HasColumn(connection, "symbols", "file_path")
            && HasColumn(connection, "files", "content"))
        {
            return new DbShape(
                DbShapeKind.Legacy,
                SymbolIdColumn: "id",
                SymbolPathColumn: "file_path",
                IdentifierIdColumn: "id",
                IdentifierPathColumn: "file_path",
                LiteralIdColumn: "id",
                LiteralPathColumn: "file_path");
        }

        throw new InvalidOperationException($"Unsupported extract DB schema at '{dbPath}'.");
    }

    private static DbFacts LoadDbFacts(string dbPath, DbShape shape)
    {
        using var connection = OpenReadOnly(dbPath);
        string rootPath = shape.Kind == DbShapeKind.V1
            ? ScalarString(connection, "SELECT value FROM artifact_metadata WHERE key = 'root_path';") ?? string.Empty
            : InferLegacyRoot(dbPath);
        long symbolCount = ScalarLong(connection, "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL;");
        long fileCount = ScalarLong(connection, "SELECT COUNT(*) FROM files;");
        long identifierCount = ScalarLong(connection, "SELECT COUNT(*) FROM identifiers;");
        long literalCount = ScalarLong(connection, "SELECT COUNT(*) FROM literals;");

        return new DbFacts(rootPath, symbolCount, fileCount, identifierCount, literalCount, new FileInfo(dbPath).Length);
    }

    private static bool HasTable(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string InferLegacyRoot(string dbPath)
    {
        string fullPath = Path.GetFullPath(dbPath);
        DirectoryInfo? dir = Directory.GetParent(fullPath);
        if (dir is not null && string.Equals(dir.Name, ".miller", StringComparison.Ordinal))
            return dir.Parent?.FullName ?? string.Empty;

        return string.Empty;
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static string? ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static BuildStats MeasureBuild<T>(Func<T> build, out T result)
    {
        ForceFullCollection();
        long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        long privateBefore = CurrentPrivateBytes();

        var sw = Stopwatch.StartNew();
        result = build();
        sw.Stop();

        ForceFullCollection();
        long heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        long privateAfter = CurrentPrivateBytes();

        return new BuildStats(sw.Elapsed, heapAfter - heapBefore, privateAfter - privateBefore);
    }

    private static QueryStats MeasureQueries(Options options, Func<string, int> runOne)
    {
        foreach (string query in options.Queries)
            runOne(query);

        var samples = new List<double>(options.Queries.Length * options.Repetitions);
        int firstPassHits = 0;
        var sw = new Stopwatch();

        for (int repetition = 0; repetition < options.Repetitions; repetition++)
        {
            foreach (string query in options.Queries)
            {
                sw.Restart();
                int hits = runOne(query);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                if (repetition == 0)
                    firstPassHits += hits;
            }
        }

        samples.Sort();
        double p50 = Percentile(samples, 0.50);
        double p95 = Percentile(samples, 0.95);
        return new QueryStats(p50, p95, firstPassHits);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;

        int index = (int)Math.Ceiling(sorted.Count * percentile) - 1;
        index = Math.Clamp(index, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static void AppendText(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(value);
    }

    private static void AppendExtra(
        StringBuilder builder,
        Dictionary<string, int> remainingExtraCharsById,
        string symbolId,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        int remaining = remainingExtraCharsById[symbolId];
        if (remaining <= 0)
            return;

        int take = Math.Min(remaining, value.Length);
        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(value, 0, take);
        remainingExtraCharsById[symbolId] = remaining - take;
    }

    private static bool IsDocsLike(string path, string language)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.Contains("/docs/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/doc/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("doc/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/documentation/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("documentation/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string extension = Path.GetExtension(normalized);
        return string.Equals(language, "markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".mdx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".rst", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".adoc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".org", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".toml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".ini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".cfg", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintHeader(string dbPath, DbFacts dbFacts, Options options)
    {
        Console.WriteLine("== Miller search projection spike ==");
        Console.WriteLine($"db:              {dbPath}");
        Console.WriteLine($"root:            {dbFacts.RootPath}");
        Console.WriteLine($"db size:         {FormatBytes(dbFacts.DbBytes)}");
        Console.WriteLine($"symbols/files:   {dbFacts.SymbolCount:N0} / {dbFacts.FileCount:N0}");
        Console.WriteLine($"ident/literals:  {dbFacts.IdentifierCount:N0} / {dbFacts.LiteralCount:N0}");
        Console.WriteLine($"queries:         {string.Join(", ", options.Queries)}");
        Console.WriteLine($"repetitions:     {options.Repetitions:N0}");
        Console.WriteLine($"content scope:   {options.ContentScope.ToString().ToLowerInvariant()}");
        Console.WriteLine($"content cap:     {FormatBytes(options.ContentMaxBytes)} per file");
        Console.WriteLine($"hash verify:     {(options.HashVerify ? "on" : "off")}");
        Console.WriteLine();
    }

    private static void PrintBuild(string name, BuildStats build, IReadOnlyList<string> details)
    {
        Console.WriteLine(name);
        Console.WriteLine($"  build {build.Elapsed.TotalMilliseconds,9:F1} ms | heap {FormatSignedBytes(build.ManagedHeapDelta),11} | private {FormatSignedBytes(build.PrivateBytesDelta),11}");
        Console.WriteLine($"  {string.Join(" | ", details)}");
        Console.WriteLine();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              dotnet run -c Release --project spike/SearchProjection.Spike -- [options]

            Options:
              --db <path>                       Extract SQLite DB. Default: .miller/symbols.db
              --queries <a,b,c>                 Comma-separated query list.
              --repetitions <n>                 Query repetitions. Default: 20
              --limit <n>                       Per-query result limit. Default: 50
              --symbol-extra-char-limit <n>     Per-symbol cap for identifier context/literals in wide projection. Default: 2000
              --content-max-bytes <n>           Max bytes per file for content corpus. Default: 1048576
              --content-scope <all|docs>        Content corpus filter. Default: docs
              --no-hash-verify                  Skip BLAKE3 freshness verification while reading disk content.
              --no-fts                          Skip SQLite FTS5 content indexes.
              --fts-dir <path>                  Directory for temporary FTS DBs.
              --keep-fts                        Keep temporary FTS DBs after the run.
              --help                            Show this help.
            """);
    }

    private static long CurrentPrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string FormatBytes(long bytes)
    {
        double absolute = Math.Abs((double)bytes);
        string sign = bytes < 0 ? "-" : string.Empty;

        if (absolute >= 1024 * 1024 * 1024)
            return sign + (absolute / 1024 / 1024 / 1024).ToString("F2", CultureInfo.InvariantCulture) + " GB";
        if (absolute >= 1024 * 1024)
            return sign + (absolute / 1024 / 1024).ToString("F1", CultureInfo.InvariantCulture) + " MB";
        if (absolute >= 1024)
            return sign + (absolute / 1024).ToString("F1", CultureInfo.InvariantCulture) + " KB";

        return sign + absolute.ToString("F0", CultureInfo.InvariantCulture) + " B";
    }

    private static string FormatSignedBytes(long bytes) => bytes >= 0 ? "+" + FormatBytes(bytes) : FormatBytes(bytes);

}

internal sealed class Options
{
    public string DbPath { get; private set; } = ".miller/symbols.db";
    public string[] Queries { get; private set; } =
    [
        "workspace",
        "search",
        "index",
        "extract",
        "markdown",
        "http",
        "service",
        "token",
        "configuration",
        "release"
    ];
    public int Repetitions { get; private set; } = 20;
    public int Limit { get; private set; } = 50;
    public int SymbolExtraCharLimit { get; private set; } = 2_000;
    public long ContentMaxBytes { get; private set; } = 1_048_576;
    public ContentScope ContentScope { get; private set; } = ContentScope.Docs;
    public bool HashVerify { get; private set; } = true;
    public bool NoFts { get; private set; }
    public string? FtsDir { get; private set; }
    public bool KeepFts { get; private set; }
    public bool ShowHelp { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--db":
                    options.DbPath = RequiredValue(args, ref i, arg);
                    break;
                case "--queries":
                    options.Queries = ParseQueries(RequiredValue(args, ref i, arg));
                    break;
                case "--repetitions":
                    options.Repetitions = ParsePositiveInt(RequiredValue(args, ref i, arg), arg);
                    break;
                case "--limit":
                    options.Limit = ParsePositiveInt(RequiredValue(args, ref i, arg), arg);
                    break;
                case "--symbol-extra-char-limit":
                    options.SymbolExtraCharLimit = ParseNonNegativeInt(RequiredValue(args, ref i, arg), arg);
                    break;
                case "--content-max-bytes":
                    options.ContentMaxBytes = ParsePositiveLong(RequiredValue(args, ref i, arg), arg);
                    break;
                case "--content-scope":
                    options.ContentScope = ParseContentScope(RequiredValue(args, ref i, arg));
                    break;
                case "--no-hash-verify":
                    options.HashVerify = false;
                    break;
                case "--no-fts":
                    options.NoFts = true;
                    break;
                case "--fts-dir":
                    options.FtsDir = RequiredValue(args, ref i, arg);
                    break;
                case "--keep-fts":
                    options.KeepFts = true;
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arg}'. Pass --help for usage.");
            }
        }

        return options;
    }

    private static string RequiredValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");

        index++;
        return args[index];
    }

    private static string[] ParseQueries(string value)
    {
        string[] queries = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static q => q.Length > 0)
            .ToArray();

        if (queries.Length == 0)
            throw new ArgumentException("--queries must include at least one non-empty query.");

        return queries;
    }

    private static int ParsePositiveInt(string value, string option)
    {
        int parsed = int.Parse(value, CultureInfo.InvariantCulture);
        if (parsed <= 0)
            throw new ArgumentException($"{option} must be > 0.");

        return parsed;
    }

    private static int ParseNonNegativeInt(string value, string option)
    {
        int parsed = int.Parse(value, CultureInfo.InvariantCulture);
        if (parsed < 0)
            throw new ArgumentException($"{option} must be >= 0.");

        return parsed;
    }

    private static long ParsePositiveLong(string value, string option)
    {
        long parsed = long.Parse(value, CultureInfo.InvariantCulture);
        if (parsed <= 0)
            throw new ArgumentException($"{option} must be > 0.");

        return parsed;
    }

    private static ContentScope ParseContentScope(string value) => value.ToLowerInvariant() switch
    {
        "all" => ContentScope.All,
        "docs" => ContentScope.Docs,
        _ => throw new ArgumentException("--content-scope must be 'all' or 'docs'.")
    };
}

internal enum ContentScope
{
    All,
    Docs
}

internal enum DbShapeKind
{
    V1,
    Legacy
}

internal sealed record DbShape(
    DbShapeKind Kind,
    string SymbolIdColumn,
    string SymbolPathColumn,
    string IdentifierIdColumn,
    string IdentifierPathColumn,
    string LiteralIdColumn,
    string LiteralPathColumn);

internal sealed record DbFacts(
    string RootPath,
    long SymbolCount,
    long FileCount,
    long IdentifierCount,
    long LiteralCount,
    long DbBytes);

internal sealed record BuildStats(TimeSpan Elapsed, long ManagedHeapDelta, long PrivateBytesDelta);

internal sealed record QueryStats(double P50Ms, double P95Ms, int FirstPassHits);

internal sealed record ProjectionDocument(int DocId, string Label, string Path, string Text);

internal sealed record ContentCorpus(
    IReadOnlyList<ProjectionDocument> Documents,
    int ManifestRows,
    long IndexedBytes,
    int StatusSkipped,
    int ScopeSkipped,
    int TooLargeSkipped,
    int MissingSkipped,
    int HashMismatchSkipped,
    int NonUtf8Skipped,
    int IoSkipped)
{
    public string SkippedSummary =>
        $"skipped status={StatusSkipped:N0}, scope={ScopeSkipped:N0}, large={TooLargeSkipped:N0}, " +
        $"missing={MissingSkipped:N0}, hash={HashMismatchSkipped:N0}, utf8={NonUtf8Skipped:N0}, io={IoSkipped:N0}";
}

internal sealed class ProjectionIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly FrozenDictionary<string, Posting[]> _postings;
    private readonly FrozenDictionary<int, int> _docLengths;
    private readonly double _avgdl;

    private ProjectionIndex(
        FrozenDictionary<string, Posting[]> postings,
        FrozenDictionary<int, int> docLengths,
        long postingCount,
        double avgdl)
    {
        _postings = postings;
        _docLengths = docLengths;
        PostingCount = postingCount;
        _avgdl = avgdl;
    }

    public int DocumentCount => _docLengths.Count;
    public int TermCount => _postings.Count;
    public long PostingCount { get; }
    public long EstimatedPostingBytes => PostingCount * (sizeof(int) * 2L);

    public static ProjectionIndex Build(IReadOnlyList<ProjectionDocument> documents)
    {
        var builder = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        var docLengths = new Dictionary<int, int>(documents.Count);
        var tokens = new List<string>(128);
        long totalLength = 0;

        foreach (ProjectionDocument document in documents)
        {
            tokens.Clear();
            CodeTokenizer.Tokenize(document.Text, tokens);
            docLengths[document.DocId] = tokens.Count;
            totalLength += tokens.Count;

            foreach (string token in tokens)
            {
                if (!builder.TryGetValue(token, out var perDoc))
                {
                    perDoc = new Dictionary<int, int>();
                    builder[token] = perDoc;
                }

                perDoc.TryGetValue(document.DocId, out int tf);
                perDoc[document.DocId] = tf + 1;
            }
        }

        long postingCount = 0;
        var postings = builder.ToFrozenDictionary(
            static kv => kv.Key,
            kv =>
            {
                var arr = new Posting[kv.Value.Count];
                int index = 0;
                foreach (var (docId, tf) in kv.Value)
                    arr[index++] = new Posting(docId, tf);
                Array.Sort(arr, static (a, b) => a.DocId.CompareTo(b.DocId));
                return arr;
            },
            StringComparer.Ordinal);

        foreach (Posting[] posting in postings.Values)
            postingCount += posting.Length;

        double avgdl = documents.Count == 0 ? 0 : (double)totalLength / documents.Count;
        return new ProjectionIndex(postings, docLengths.ToFrozenDictionary(), postingCount, avgdl);
    }

    public int Search(string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0 || _docLengths.Count == 0)
            return 0;

        var queryTokens = new List<string>(8);
        CodeTokenizer.Tokenize(query, queryTokens);
        if (queryTokens.Count == 0)
            return 0;

        var distinct = new HashSet<string>(queryTokens, StringComparer.Ordinal);
        var scores = new Dictionary<int, double>();
        foreach (string term in distinct)
        {
            if (!_postings.TryGetValue(term, out Posting[]? postings))
                continue;

            double idf = Math.Log(1.0 + (_docLengths.Count - postings.Length + 0.5) / (postings.Length + 0.5));
            foreach (Posting posting in postings)
            {
                int docLength = _docLengths[posting.DocId];
                double denominator = posting.Tf + K1 * (1 - B + B * docLength / _avgdl);
                double score = idf * posting.Tf * (K1 + 1) / denominator;
                scores.TryGetValue(posting.DocId, out double current);
                scores[posting.DocId] = current + score;
            }
        }

        if (scores.Count == 0)
            return 0;

        return scores
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key)
            .Take(limit)
            .Count();
    }

    private readonly record struct Posting(int DocId, int Tf);
}

internal sealed class FtsIndex : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly bool _keep;

    private FtsIndex(string dbPath, SqliteConnection connection, long fileBytes, bool keep)
    {
        DbPath = dbPath;
        _connection = connection;
        FileBytes = fileBytes;
        _keep = keep;
    }

    public string DbPath { get; }
    public long FileBytes { get; }

    public static FtsIndex Build(IReadOnlyList<ProjectionDocument> documents, Options options, bool trigram)
    {
        string dir = options.FtsDir is null
            ? Path.Combine(Path.GetTempPath(), "miller-search-projection-spike")
            : Path.GetFullPath(options.FtsDir);
        Directory.CreateDirectory(dir);

        string slug = trigram ? "trigram" : "normal";
        string dbPath = Path.Combine(dir, $"content-fts5-{slug}-{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.db");
        DeleteSqliteFiles(dbPath);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        Exec(connection, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        Exec(connection, trigram
            ? "CREATE VIRTUAL TABLE docs USING fts5(path UNINDEXED, body, tokenize='trigram');"
            : "CREATE VIRTUAL TABLE docs USING fts5(path UNINDEXED, body);");

        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO docs(rowid, path, body) VALUES($id, $path, $body);";
            var idParameter = command.CreateParameter();
            idParameter.ParameterName = "$id";
            command.Parameters.Add(idParameter);
            var pathParameter = command.CreateParameter();
            pathParameter.ParameterName = "$path";
            command.Parameters.Add(pathParameter);
            var bodyParameter = command.CreateParameter();
            bodyParameter.ParameterName = "$body";
            command.Parameters.Add(bodyParameter);

            foreach (ProjectionDocument document in documents)
            {
                idParameter.Value = document.DocId + 1L;
                pathParameter.Value = document.Path;
                bodyParameter.Value = document.Text;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Exec(connection, "INSERT INTO docs(docs) VALUES('optimize');");
        long fileBytes = TotalSqliteBytes(dbPath);
        return new FtsIndex(dbPath, connection, fileBytes, options.KeepFts);
    }

    public int Search(string query, int limit)
    {
        string ftsQuery = FtsQuery(query);
        if (ftsQuery.Length == 0)
            return 0;

        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT rowid FROM docs WHERE docs MATCH $query ORDER BY rank LIMIT {limit.ToString(CultureInfo.InvariantCulture)};";
        command.Parameters.AddWithValue("$query", ftsQuery);

        int hits = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            hits++;

        return hits;
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (!_keep)
            DeleteSqliteFiles(DbPath);
    }

    private static string FtsQuery(string query)
    {
        var tokens = new List<string>(8);
        CodeTokenizer.Tokenize(query, tokens);
        return string.Join(" OR ", tokens.Distinct(StringComparer.Ordinal).Select(static token => EscapeFtsToken(token)));
    }

    private static string EscapeFtsToken(string token) => "\"" + token.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long TotalSqliteBytes(string dbPath)
    {
        long bytes = 0;
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            string path = dbPath + suffix;
            if (File.Exists(path))
                bytes += new FileInfo(path).Length;
        }

        return bytes;
    }

    private static void DeleteSqliteFiles(string dbPath)
    {
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            string path = dbPath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
