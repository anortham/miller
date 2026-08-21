using Microsoft.Data.Sqlite;
using Miller.Core.Resolution;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Resolution;

internal static class RevisionFactCacheLoader
{
    internal readonly record struct VisibleFile(long VersionId, string Path, string Language);

    internal readonly record struct ImportSeed(string Name, ImportMetadata? Metadata);

    internal static List<VisibleFile> ReadVisibleStore(SqliteConnection connection, StoreVisibility visibility)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.version_id,e.path,e.language
            FROM main.manifest_entries AS e
            WHERE e.view_id=$view_id AND e.generation=$generation AND e.version_id IS NOT NULL
            ORDER BY e.path
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<VisibleFile>();
        while (reader.Read())
            rows.Add(new VisibleFile(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    internal static List<VisibleFile> ReadVisibleArtifact(SqliteConnection connection)
    {
        if (!TableExists(connection, "files"))
            return [];

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.rowid,f.path,f.language
            FROM files AS f
            ORDER BY f.path
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<VisibleFile>();
        while (reader.Read())
            rows.Add(new VisibleFile(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    internal static Dictionary<long, VersionSlice> LoadAllStoreSlices(
        SqliteConnection connection,
        StoreVisibility visibility,
        IReadOnlyList<VisibleFile> files,
        StringInternPool intern)
    {
        var slices = new Dictionary<long, VersionSlice>(files.Count);
        foreach (VisibleFile file in files)
        {
            slices[file.VersionId] = new VersionSlice(
                file.VersionId,
                intern.Intern(file.Path),
                intern.Intern(file.Language),
                [],
                [],
                [],
                [],
                []);
        }

        FillStoreSymbols(connection, visibility, slices, intern);
        FillStoreTypeFacts(connection, visibility, slices, intern);
        FillStorePropagation(connection, visibility, slices);
        return slices;
    }

    internal static Dictionary<long, VersionSlice> LoadAllArtifactSlices(
        SqliteConnection connection,
        IReadOnlyList<VisibleFile> files,
        StringInternPool intern)
    {
        var slices = new Dictionary<long, VersionSlice>(files.Count);
        foreach (VisibleFile file in files)
        {
            slices[file.VersionId] = new VersionSlice(
                file.VersionId,
                intern.Intern(file.Path),
                intern.Intern(file.Language),
                [],
                [],
                [],
                [],
                []);
        }

        if (slices.Count == 0)
            return slices;

        FillArtifactSymbols(connection, slices, intern);
        FillArtifactTypeFacts(connection, slices, intern);
        FillArtifactPropagation(connection, slices);
        return slices;
    }

    private static void FillStoreSymbols(
        SqliteConnection connection,
        StoreVisibility visibility,
        Dictionary<long, VersionSlice> slices,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT s.version_id,s.symbol_id,s.name,s.kind,s.language,s.parent_symbol_id,s.signature,s.visibility,s.metadata_json
            FROM main.symbols AS s
            JOIN main.manifest_entries AS e ON e.version_id=s.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            ORDER BY s.version_id,s.symbol_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        FillSymbols(command, slices, intern);
    }

    /// <summary>
    /// The one name arm of the whole-generation symbol load, read for a single name instead of for every
    /// visible file. The projection, the visibility join, the ordering, and the row transform are the ones
    /// <see cref="FillStoreSymbols"/> uses, so the list this returns is the list the eager name index would
    /// hold for that name — including the duplicate rows a version with two manifest paths produces.
    /// </summary>
    internal static List<(long VersionId, PackedSymbol Symbol)> ReadStoreSymbolsNamed(
        SqliteConnection connection,
        StoreVisibility visibility,
        string name,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT s.version_id,s.symbol_id,s.name,s.kind,s.language,s.parent_symbol_id,s.signature,s.visibility,s.metadata_json
            FROM main.symbols AS s
            JOIN main.manifest_entries AS e ON e.version_id=s.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation AND s.name=$name
            ORDER BY s.version_id,s.symbol_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$name", name);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<(long VersionId, PackedSymbol Symbol)>();
        var packed = new List<PackedSymbol>(1);
        var seeds = new List<ImportSeed>(1);
        while (reader.Read())
        {
            long versionId = reader.GetInt64(0);
            packed.Clear();
            seeds.Clear();
            AppendSymbol(reader, intern, packed, seeds);
            if (packed.Count == 1)
                rows.Add((versionId, packed[0]));
        }

        return rows;
    }

    private static void FillArtifactSymbols(
        SqliteConnection connection,
        Dictionary<long, VersionSlice> slices,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT f.rowid,s.symbol_id,s.name,s.kind,s.language,s.parent_symbol_id,s.signature,s.visibility,s.metadata_json
            FROM symbols AS s
            JOIN files AS f ON f.file_id=s.file_id
            ORDER BY 1,s.symbol_id
            """;
        FillSymbols(command, slices, intern);
    }

    private static void FillSymbols(
        SqliteCommand command,
        Dictionary<long, VersionSlice> slices,
        StringInternPool intern)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var symbols = new List<PackedSymbol>();
        var importSeeds = new List<ImportSeed>();
        long currentVersion = long.MinValue;
        while (reader.Read())
        {
            long versionId = reader.GetInt64(0);
            if (versionId != currentVersion)
            {
                FlushSymbols(slices, currentVersion, symbols, importSeeds);
                currentVersion = versionId;
            }

            AppendSymbol(reader, intern, symbols, importSeeds);
        }

        FlushSymbols(slices, currentVersion, symbols, importSeeds);
    }

    private static void AppendSymbol(
        SqliteDataReader reader,
        StringInternPool intern,
        List<PackedSymbol> symbols,
        List<ImportSeed> importSeeds)
    {
        FactSymbolKind? kind = ResolutionPolicy.ParseSymbolKind(reader.GetString(3));
        if (kind is null)
            return;

        string symbolId = reader.GetString(1);
        string name = intern.Intern(reader.GetString(2));
        string language = intern.Intern(reader.GetString(4));
        string? parentId = intern.InternNullable(reader.IsDBNull(5) ? null : EmptyToNull(reader.GetString(5)));
        string? signature = intern.InternNullable(reader.IsDBNull(6) ? null : reader.GetString(6));
        string? visibility = intern.InternNullable(reader.IsDBNull(7) ? null : reader.GetString(7));
        string? metadata = reader.IsDBNull(8) ? null : reader.GetString(8);
        bool? isStatic = ResolutionPolicy.ParseIsStatic(FactMetadataParser.IsStaticRaw(metadata));
        byte staticCode = isStatic switch
        {
            true => 1,
            false => 2,
            _ => 0,
        };
        symbols.Add(new PackedSymbol(symbolId, name, kind.Value, language, parentId, signature, visibility, staticCode));
        if (kind == FactSymbolKind.Import)
            importSeeds.Add(new ImportSeed(name, FactMetadataParser.ParseImport(metadata)));
    }

    private static void FlushSymbols(
        Dictionary<long, VersionSlice> slices,
        long versionId,
        List<PackedSymbol> symbols,
        List<ImportSeed> importSeeds)
    {
        if (versionId == long.MinValue || !slices.TryGetValue(versionId, out VersionSlice? slice))
        {
            symbols.Clear();
            importSeeds.Clear();
            return;
        }

        slice.Packed = symbols.Count == 0 ? [] : symbols.ToArray();
        slice.ImportSeeds = importSeeds.Count == 0 ? [] : importSeeds.ToArray();
        symbols.Clear();
        importSeeds.Clear();
    }

    private static void FillStoreTypeFacts(
        SqliteConnection connection,
        StoreVisibility visibility,
        Dictionary<long, VersionSlice> slices,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT t.version_id,t.symbol_id,t.resolved_type,t.is_inferred
            FROM main.type_facts AS t
            JOIN main.manifest_entries AS e ON e.version_id=t.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            ORDER BY t.version_id,t.symbol_id,t.type_fact_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        FillTypeFacts(command, slices, intern);
    }

    private static void FillArtifactTypeFacts(
        SqliteConnection connection,
        Dictionary<long, VersionSlice> slices,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT f.rowid,t.symbol_id,t.resolved_type,t.is_inferred
            FROM type_facts AS t
            JOIN symbols AS s ON s.symbol_id=t.symbol_id
            JOIN files AS f ON f.file_id=s.file_id
            ORDER BY 1,t.symbol_id,t.type_fact_id
            """;
        FillTypeFacts(command, slices, intern);
    }

    private static void FillTypeFacts(
        SqliteCommand command,
        Dictionary<long, VersionSlice> slices,
        StringInternPool intern)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<PackedTypeFact>();
        long currentVersion = long.MinValue;
        while (reader.Read())
        {
            long versionId = reader.GetInt64(0);
            if (versionId != currentVersion)
            {
                FlushTypeFacts(slices, currentVersion, rows);
                currentVersion = versionId;
            }

            rows.Add(new PackedTypeFact(
                reader.GetString(1),
                intern.Intern(reader.GetString(2)),
                ReadInferred(reader, 3)));
        }

        FlushTypeFacts(slices, currentVersion, rows);
    }

    private static void FlushTypeFacts(
        Dictionary<long, VersionSlice> slices,
        long versionId,
        List<PackedTypeFact> rows)
    {
        if (versionId != long.MinValue && slices.TryGetValue(versionId, out VersionSlice? slice) && rows.Count > 0)
            slice.TypeFactRows = rows.ToArray();
        rows.Clear();
    }

    private static void FillStorePropagation(
        SqliteConnection connection,
        StoreVisibility visibility,
        Dictionary<long, VersionSlice> slices)
    {
        Dictionary<long, List<PendingLocateRow>> pending = ReadAllStorePendings(connection, visibility);
        Dictionary<long, List<RelationshipLocateRow>> relationships = ReadAllStoreRelationships(connection, visibility);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT i.version_id,i.rowid,i.name,i.start_byte,i.end_byte,i.start_line
            FROM main.identifiers AS i
            JOIN main.manifest_entries AS e ON e.version_id=i.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            ORDER BY i.version_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        LocateStreaming(command, slices, pending, relationships);
    }

    private static void FillArtifactPropagation(
        SqliteConnection connection,
        Dictionary<long, VersionSlice> slices)
    {
        Dictionary<long, List<PendingLocateRow>> pending = ReadAllArtifactPendings(connection);
        Dictionary<long, List<RelationshipLocateRow>> relationships = ReadAllArtifactRelationships(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT f.rowid,i.rowid,i.name,i.start_byte,i.end_byte,i.start_line
            FROM identifiers AS i
            JOIN files AS f ON f.file_id=i.file_id
            ORDER BY 1
            """;
        LocateStreaming(command, slices, pending, relationships);
    }

    private static void LocateStreaming(
        SqliteCommand command,
        Dictionary<long, VersionSlice> slices,
        Dictionary<long, List<PendingLocateRow>> pending,
        Dictionary<long, List<RelationshipLocateRow>> relationships)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var candidates = new List<PropagationCandidate>();
        var rowIds = new List<long>();
        long currentVersion = long.MinValue;
        while (reader.Read())
        {
            long versionId = reader.GetInt64(0);
            if (versionId != currentVersion)
            {
                FlushLocated(slices, currentVersion, candidates, rowIds, pending, relationships);
                currentVersion = versionId;
            }

            rowIds.Add(reader.GetInt64(1));
            candidates.Add(new PropagationCandidate(
                reader.GetString(2),
                ReadNullableInt64(reader, 3) ?? 0,
                ReadNullableInt64(reader, 4) ?? 0,
                reader.IsDBNull(5) ? 0 : reader.GetInt64(5)));
        }

        FlushLocated(slices, currentVersion, candidates, rowIds, pending, relationships);
    }

    private static void FlushLocated(
        Dictionary<long, VersionSlice> slices,
        long versionId,
        List<PropagationCandidate> candidates,
        List<long> rowIds,
        Dictionary<long, List<PendingLocateRow>> pending,
        Dictionary<long, List<RelationshipLocateRow>> relationships)
    {
        if (versionId != long.MinValue && slices.TryGetValue(versionId, out VersionSlice? slice))
        {
            var located = new Dictionary<long, PropagationSource>();
            if (pending.TryGetValue(versionId, out List<PendingLocateRow>? pendingRows))
            {
                foreach (PendingLocateRow row in pendingRows)
                {
                    int? index = PropagationLocator.Locate(candidates, row.Name, row.StartByte, row.EndByte, row.StartLine);
                    if (index is { } hit)
                        located[rowIds[hit]] = new PropagationSource(PropagationOrigin.Pending, row.RowId);
                }
            }

            if (relationships.TryGetValue(versionId, out List<RelationshipLocateRow>? relRows))
            {
                foreach (RelationshipLocateRow row in relRows)
                {
                    int? index = PropagationLocator.Locate(candidates, row.Name, row.StartByte, row.EndByte, row.StartLine);
                    if (index is { } hit)
                        located[rowIds[hit]] = new PropagationSource(PropagationOrigin.Relationship, row.RowId);
                }
            }

            if (located.Count == 0)
            {
                slice.LocatedRowIds = [];
                slice.LocatedSources = [];
            }
            else
            {
                long[] ids = new long[located.Count];
                PropagationSource[] sources = new PropagationSource[located.Count];
                int i = 0;
                foreach ((long rowId, PropagationSource source) in located.OrderBy(static pair => pair.Key))
                {
                    ids[i] = rowId;
                    sources[i] = source;
                    i++;
                }

                slice.LocatedRowIds = ids;
                slice.LocatedSources = sources;
            }
        }

        candidates.Clear();
        rowIds.Clear();
    }

    private static Dictionary<long, List<PendingLocateRow>> ReadAllStorePendings(
        SqliteConnection connection,
        StoreVisibility visibility)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT p.version_id,p.pending_relationship_id,p.target_terminal_name,p.start_byte,p.end_byte,p.start_line
            FROM main.pending_relationships AS p
            JOIN main.manifest_entries AS e ON e.version_id=p.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        return ReadAllPendings(command);
    }

    private static Dictionary<long, List<PendingLocateRow>> ReadAllArtifactPendings(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.rowid,p.pending_relationship_id,p.target_terminal_name,p.start_byte,p.end_byte,p.start_line
            FROM pending_relationships AS p
            JOIN files AS f ON f.file_id=p.file_id
            """;
        return ReadAllPendings(command);
    }

    private static Dictionary<long, List<PendingLocateRow>> ReadAllPendings(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var grouped = new Dictionary<long, List<PendingLocateRow>>();
        while (reader.Read())
        {
            long versionId = reader.GetInt64(0);
            if (!grouped.TryGetValue(versionId, out List<PendingLocateRow>? list))
            {
                list = [];
                grouped[versionId] = list;
            }

            list.Add(new PendingLocateRow(
                reader.GetString(1),
                reader.GetString(2),
                ReadNullableInt64(reader, 3),
                ReadNullableInt64(reader, 4),
                reader.GetInt64(5)));
        }

        return grouped;
    }

    private static Dictionary<long, List<RelationshipLocateRow>> ReadAllStoreRelationships(
        SqliteConnection connection,
        StoreVisibility visibility)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT r.version_id,r.relationship_id,t.name,r.start_byte,r.end_byte,r.start_line
            FROM main.relationships AS r
            JOIN main.manifest_entries AS e ON e.version_id=r.version_id
            JOIN main.symbols AS t ON t.symbol_id=r.to_symbol_id
            JOIN main.manifest_entries AS te
              ON te.version_id=t.version_id AND te.view_id=$view_id AND te.generation=$generation
            WHERE e.view_id=$view_id AND e.generation=$generation
              AND r.kind IN ('calls','instantiates','uses','extends','implements')
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        return ReadAllRelationships(command);
    }

    private static Dictionary<long, List<RelationshipLocateRow>> ReadAllArtifactRelationships(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.rowid,r.relationship_id,t.name,r.start_byte,r.end_byte,r.start_line
            FROM relationships AS r
            JOIN files AS f ON f.file_id=r.file_id
            JOIN symbols AS t ON t.symbol_id=r.to_symbol_id
            WHERE r.kind IN ('calls','instantiates','uses','extends','implements')
            """;
        return ReadAllRelationships(command);
    }

    private static Dictionary<long, List<RelationshipLocateRow>> ReadAllRelationships(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var grouped = new Dictionary<long, List<RelationshipLocateRow>>();
        var seen = new HashSet<(long VersionId, string Id)>();
        while (reader.Read())
        {
            long versionId = reader.GetInt64(0);
            string id = reader.GetString(1);
            if (!seen.Add((versionId, id)))
                continue;
            if (!grouped.TryGetValue(versionId, out List<RelationshipLocateRow>? list))
            {
                list = [];
                grouped[versionId] = list;
            }

            list.Add(new RelationshipLocateRow(
                id,
                reader.GetString(2),
                ReadNullableInt64(reader, 3),
                ReadNullableInt64(reader, 4),
                ReadNullableInt64(reader, 5) ?? 0));
        }

        return grouped;
    }

    /// <summary>
    /// <paramref name="indexedLocate"/> buckets the file's propagation candidates by name instead of
    /// rescanning them per source row. It answers identically and is only set by the bounded cache, whose
    /// per-query file set is dominated by this repo's largest files.
    /// </summary>
    internal static VersionSlice LoadStoreSlice(
        SqliteConnection connection,
        StoreVisibility visibility,
        VisibleFile file,
        StringInternPool intern,
        bool indexedLocate = false)
    {
        (PackedSymbol[] symbols, ImportSeed[] importSeeds) = LoadStoreSymbols(connection, visibility, file, intern);
        PackedTypeFact[] typeFacts = LoadStoreTypeFacts(connection, visibility, file.VersionId, intern);
        (long[] rowIds, PropagationSource[] sources) = LocateStore(
            connection,
            visibility,
            file.VersionId,
            indexedLocate);
        return new VersionSlice(
            file.VersionId,
            intern.Intern(file.Path),
            intern.Intern(file.Language),
            symbols,
            typeFacts,
            rowIds,
            sources,
            importSeeds);
    }

    internal static VersionSlice LoadArtifactSlice(
        SqliteConnection connection,
        VisibleFile file,
        StringInternPool intern)
    {
        (PackedSymbol[] symbols, ImportSeed[] importSeeds) = LoadArtifactSymbols(connection, file, intern);
        PackedTypeFact[] typeFacts = LoadArtifactTypeFacts(connection, file.VersionId, intern);
        (long[] rowIds, PropagationSource[] sources) = LocateArtifact(connection, file.VersionId);
        return new VersionSlice(
            file.VersionId,
            intern.Intern(file.Path),
            intern.Intern(file.Language),
            symbols,
            typeFacts,
            rowIds,
            sources,
            importSeeds);
    }

    internal static ImportBinding[] BindImports(
        VersionSlice slice,
        IReadOnlyDictionary<string, VisibleFile> pathIndex)
    {
        if (slice.ImportSeeds.Length == 0 && slice.Imports.Length > 0)
        {
            var rebound = new ImportBinding[slice.Imports.Length];
            for (int i = 0; i < slice.Imports.Length; i++)
            {
                ImportBinding existing = slice.Imports[i];
                rebound[i] = existing with
                {
                    ModuleVersionId = ResolveModule(slice.Path, existing.Source, slice.Language, pathIndex),
                };
            }

            return rebound;
        }

        var imports = new List<ImportBinding>(slice.ImportSeeds.Length);
        foreach (ImportSeed seed in slice.ImportSeeds)
        {
            ImportBinding unbound = ImportBinding.FromSymbol(seed.Name, seed.Metadata);
            long? moduleVersion = ResolveModule(slice.Path, unbound.Source, slice.Language, pathIndex);
            imports.Add(unbound with { ModuleVersionId = moduleVersion });
        }

        return imports.Count == 0 ? [] : imports.ToArray();
    }

    private static long? ResolveModule(
        string importingPath,
        string? source,
        string language,
        IReadOnlyDictionary<string, VisibleFile> pathIndex)
    {
        if (source is null)
            return null;
        foreach (string candidate in ImportBinding.ModuleCandidates(importingPath, source, language))
        {
            if (pathIndex.TryGetValue(candidate, out VisibleFile file)
                && string.Equals(file.Language, language, StringComparison.Ordinal))
            {
                return file.VersionId;
            }
        }

        return null;
    }

    private static (PackedSymbol[] Symbols, ImportSeed[] ImportSeeds) LoadStoreSymbols(
        SqliteConnection connection,
        StoreVisibility visibility,
        VisibleFile file,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.symbol_id,s.name,s.kind,s.language,s.parent_symbol_id,s.signature,s.visibility,s.metadata_json
            FROM main.symbols AS s
            JOIN main.manifest_entries AS e ON e.version_id=s.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation AND s.version_id=$version
            ORDER BY s.symbol_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$version", file.VersionId);
        return ReadSymbols(command, file.VersionId, intern);
    }

    private static (PackedSymbol[] Symbols, ImportSeed[] ImportSeeds) LoadArtifactSymbols(
        SqliteConnection connection,
        VisibleFile file,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.symbol_id,s.name,s.kind,s.language,s.parent_symbol_id,s.signature,s.visibility,s.metadata_json
            FROM symbols AS s
            JOIN files AS f ON f.file_id=s.file_id
            WHERE f.rowid=$version
            ORDER BY s.symbol_id
            """;
        command.Parameters.AddWithValue("$version", file.VersionId);
        return ReadSymbols(command, file.VersionId, intern);
    }

    private static (PackedSymbol[] Symbols, ImportSeed[] ImportSeeds) ReadSymbols(
        SqliteCommand command,
        long versionId,
        StringInternPool intern)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var symbols = new List<PackedSymbol>();
        var importSeeds = new List<ImportSeed>();
        while (reader.Read())
        {
            string kindText = reader.GetString(2);
            FactSymbolKind? kind = ResolutionPolicy.ParseSymbolKind(kindText);
            if (kind is null)
                continue;

            string symbolId = reader.GetString(0);
            string name = intern.Intern(reader.GetString(1));
            string language = intern.Intern(reader.GetString(3));
            string? parentId = intern.InternNullable(reader.IsDBNull(4) ? null : EmptyToNull(reader.GetString(4)));
            string? signature = intern.InternNullable(reader.IsDBNull(5) ? null : reader.GetString(5));
            string? visibility = intern.InternNullable(reader.IsDBNull(6) ? null : reader.GetString(6));
            string? metadata = reader.IsDBNull(7) ? null : reader.GetString(7);
            bool? isStatic = ResolutionPolicy.ParseIsStatic(FactMetadataParser.IsStaticRaw(metadata));
            byte staticCode = isStatic switch
            {
                true => 1,
                false => 2,
                _ => 0,
            };
            symbols.Add(new PackedSymbol(symbolId, name, kind.Value, language, parentId, signature, visibility, staticCode));
            if (kind == FactSymbolKind.Import)
                importSeeds.Add(new ImportSeed(name, FactMetadataParser.ParseImport(metadata)));
        }

        _ = versionId;
        return (
            symbols.Count == 0 ? [] : symbols.ToArray(),
            importSeeds.Count == 0 ? [] : importSeeds.ToArray());
    }

    private static PackedTypeFact[] LoadStoreTypeFacts(
        SqliteConnection connection,
        StoreVisibility visibility,
        long versionId,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.symbol_id,t.resolved_type,t.is_inferred
            FROM main.type_facts AS t
            JOIN main.manifest_entries AS e ON e.version_id=t.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation AND t.version_id=$version
            ORDER BY t.symbol_id,t.type_fact_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$version", versionId);
        return ReadTypeFacts(command, intern);
    }

    private static PackedTypeFact[] LoadArtifactTypeFacts(
        SqliteConnection connection,
        long versionId,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.symbol_id,t.resolved_type,t.is_inferred
            FROM type_facts AS t
            JOIN symbols AS s ON s.symbol_id=t.symbol_id
            JOIN files AS f ON f.file_id=s.file_id
            WHERE f.rowid=$version
            ORDER BY t.symbol_id,t.type_fact_id
            """;
        command.Parameters.AddWithValue("$version", versionId);
        return ReadTypeFacts(command, intern);
    }

    private static PackedTypeFact[] ReadTypeFacts(SqliteCommand command, StringInternPool intern)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<PackedTypeFact>();
        while (reader.Read())
        {
            rows.Add(new PackedTypeFact(
                reader.GetString(0),
                intern.Intern(reader.GetString(1)),
                ReadInferred(reader, 2)));
        }

        return rows.Count == 0 ? [] : rows.ToArray();
    }

    private static (long[] RowIds, PropagationSource[] Sources) LocateStore(
        SqliteConnection connection,
        StoreVisibility visibility,
        long versionId,
        bool indexedLocate = false)
    {
        List<PendingLocateRow> pending = ReadStorePendings(connection, visibility, versionId);
        List<RelationshipLocateRow> relationships =
            (indexedLocate ? TryReadStoreRelationshipsByVersion(connection, visibility, versionId) : null)
            ?? ReadStoreRelationships(connection, visibility, versionId);
        if (pending.Count == 0 && relationships.Count == 0)
            return ([], []);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.rowid,i.name,i.start_byte,i.end_byte,i.start_line
            FROM main.identifiers AS i
            JOIN main.manifest_entries AS e ON e.version_id=i.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation AND i.version_id=$version
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$version", versionId);
        return Locate(command, pending, relationships, indexedLocate);
    }

    private static (long[] RowIds, PropagationSource[] Sources) LocateArtifact(SqliteConnection connection, long versionId)
    {
        List<PendingLocateRow> pending = ReadArtifactPendings(connection, versionId);
        List<RelationshipLocateRow> relationships = ReadArtifactRelationships(connection, versionId);
        if (pending.Count == 0 && relationships.Count == 0)
            return ([], []);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.rowid,i.name,i.start_byte,i.end_byte,i.start_line
            FROM identifiers AS i
            JOIN files AS f ON f.file_id=i.file_id
            WHERE f.rowid=$version
            """;
        command.Parameters.AddWithValue("$version", versionId);
        return Locate(command, pending, relationships);
    }

    private static (long[] RowIds, PropagationSource[] Sources) Locate(
        SqliteCommand identifierCommand,
        List<PendingLocateRow> pending,
        List<RelationshipLocateRow> relationships,
        bool indexedLocate = false)
    {
        using SqliteDataReader reader = identifierCommand.ExecuteReader();
        var candidates = new List<PropagationCandidate>();
        var rowIds = new List<long>();
        while (reader.Read())
        {
            rowIds.Add(reader.GetInt64(0));
            candidates.Add(new PropagationCandidate(
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        PropagationCandidateIndex? index = indexedLocate ? new PropagationCandidateIndex(candidates) : null;
        var located = new Dictionary<long, PropagationSource>();
        foreach (PendingLocateRow row in pending)
        {
            int? hit = index is null
                ? PropagationLocator.Locate(candidates, row.Name, row.StartByte, row.EndByte, row.StartLine)
                : index.Locate(row.Name, row.StartByte, row.EndByte, row.StartLine);
            if (hit is { } hitIndex)
                located[rowIds[hitIndex]] = new PropagationSource(PropagationOrigin.Pending, row.RowId);
        }

        foreach (RelationshipLocateRow row in relationships)
        {
            int? hit = index is null
                ? PropagationLocator.Locate(candidates, row.Name, row.StartByte, row.EndByte, row.StartLine)
                : index.Locate(row.Name, row.StartByte, row.EndByte, row.StartLine);
            if (hit is { } hitIndex)
                located[rowIds[hitIndex]] = new PropagationSource(PropagationOrigin.Relationship, row.RowId);
        }

        return ToLocatedArrays(located);
    }

    private static (long[] RowIds, PropagationSource[] Sources) ToLocatedArrays(
        Dictionary<long, PropagationSource> located)
    {
        if (located.Count == 0)
            return ([], []);
        long[] ids = new long[located.Count];
        PropagationSource[] sources = new PropagationSource[located.Count];
        int i = 0;
        foreach ((long rowId, PropagationSource source) in located.OrderBy(static pair => pair.Key))
        {
            ids[i] = rowId;
            sources[i] = source;
            i++;
        }

        return (ids, sources);
    }

    private static List<PendingLocateRow> ReadStorePendings(
        SqliteConnection connection,
        StoreVisibility visibility,
        long versionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.pending_relationship_id,p.target_terminal_name,p.start_byte,p.end_byte,p.start_line
            FROM main.pending_relationships AS p
            JOIN main.manifest_entries AS e ON e.version_id=p.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation AND p.version_id=$version
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$version", versionId);
        return ReadPendings(command);
    }

    private static List<PendingLocateRow> ReadArtifactPendings(SqliteConnection connection, long versionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.pending_relationship_id,p.target_terminal_name,p.start_byte,p.end_byte,p.start_line
            FROM pending_relationships AS p
            JOIN files AS f ON f.file_id=p.file_id
            WHERE f.rowid=$version
            """;
        command.Parameters.AddWithValue("$version", versionId);
        return ReadPendings(command);
    }

    private static List<PendingLocateRow> ReadPendings(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<PendingLocateRow>();
        while (reader.Read())
        {
            rows.Add(new PendingLocateRow(
                reader.GetString(0),
                reader.GetString(1),
                ReadNullableInt64(reader, 2),
                ReadNullableInt64(reader, 3),
                reader.GetInt64(4)));
        }

        return rows;
    }

    internal static List<RelationshipLocateRow> ReadStoreRelationships(
        SqliteConnection connection,
        StoreVisibility visibility,
        long versionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.relationship_id,t.name,r.start_byte,r.end_byte,r.start_line
            FROM main.relationships AS r
            JOIN main.manifest_entries AS e ON e.version_id=r.version_id
            JOIN main.symbols AS t ON t.symbol_id=r.to_symbol_id
            JOIN main.manifest_entries AS te
              ON te.version_id=t.version_id AND te.view_id=$view_id AND te.generation=$generation
            WHERE e.view_id=$view_id AND e.generation=$generation AND r.version_id=$version
              AND r.kind IN ('calls','instantiates','uses','extends','implements')
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$version", versionId);
        return ReadRelationships(command);
    }

    /// <summary>
    /// The one-file propagation source rows, asked in a shape SQLite can drive from
    /// <c>relationships(version_id)</c>. Returns null when the fast shape cannot be proved equal to
    /// <see cref="ReadStoreRelationships"/>, and the caller falls back to that one.
    /// </summary>
    /// <remarks>
    /// <see cref="ReadStoreRelationships"/> joins two manifest_entries aliases, and SQLite plans it by
    /// driving from the whole generation's manifest and its 127k symbols before it ever reaches the one file
    /// asked for — 240 ms per file on this repo, against 1 ms for the same answer here. The visibility joins
    /// become EXISTS tests, which is the same predicate without the row multiplication.
    /// <para>Equality: both forms yield one row per (relationship, visible target symbol row) and the caller
    /// keeps the first row per relationship id. Every field except <c>name</c> comes from the relationship
    /// itself, so the two forms can only disagree when one relationship's target id resolves to visible
    /// symbol rows with DIFFERENT names — which this method detects and refuses. Row ORDER may differ, and
    /// does not matter: the located map is keyed by identifier row and only its key set and origin are read
    /// (<c>PropagationSource.RowId</c> is stored and never consulted).</para>
    /// </remarks>
    internal static List<RelationshipLocateRow>? TryReadStoreRelationshipsByVersion(
        SqliteConnection connection,
        StoreVisibility visibility,
        long versionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.relationship_id,t.name,r.start_byte,r.end_byte,r.start_line
            FROM main.relationships AS r
            JOIN main.symbols AS t ON t.symbol_id=r.to_symbol_id
            WHERE r.version_id=$version
              AND r.kind IN ('calls','instantiates','uses','extends','implements')
              AND EXISTS (
                    SELECT 1 FROM main.manifest_entries AS e
                    WHERE e.version_id=r.version_id AND e.view_id=$view_id AND e.generation=$generation)
              AND EXISTS (
                    SELECT 1 FROM main.manifest_entries AS te
                    WHERE te.version_id=t.version_id AND te.view_id=$view_id AND te.generation=$generation)
            ORDER BY r.relationship_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$version", versionId);
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<RelationshipLocateRow>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            string id = reader.GetString(0);
            string name = reader.GetString(1);
            if (names.TryGetValue(id, out string? kept))
            {
                if (!string.Equals(kept, name, StringComparison.Ordinal))
                    return null;
                continue;
            }

            names.Add(id, name);
            rows.Add(new RelationshipLocateRow(
                id,
                name,
                ReadNullableInt64(reader, 2),
                ReadNullableInt64(reader, 3),
                ReadNullableInt64(reader, 4) ?? 0));
        }

        return rows;
    }

    private static List<RelationshipLocateRow> ReadArtifactRelationships(SqliteConnection connection, long versionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.relationship_id,t.name,r.start_byte,r.end_byte,r.start_line
            FROM relationships AS r
            JOIN files AS f ON f.file_id=r.file_id
            JOIN symbols AS t ON t.symbol_id=r.to_symbol_id
            WHERE f.rowid=$version
              AND r.kind IN ('calls','instantiates','uses','extends','implements')
            """;
        command.Parameters.AddWithValue("$version", versionId);
        return ReadRelationships(command);
    }

    private static List<RelationshipLocateRow> ReadRelationships(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<RelationshipLocateRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            string id = reader.GetString(0);
            if (!seen.Add(id))
                continue;
            rows.Add(new RelationshipLocateRow(
                id,
                reader.GetString(1),
                ReadNullableInt64(reader, 2),
                ReadNullableInt64(reader, 3),
                ReadNullableInt64(reader, 4) ?? 0));
        }

        return rows;
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static bool ReadInferred(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return false;
        return reader.GetFieldType(ordinal) == typeof(bool)
            ? reader.GetBoolean(ordinal)
            : reader.GetInt64(ordinal) != 0;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private readonly record struct PendingLocateRow(
        string RowId,
        string Name,
        long? StartByte,
        long? EndByte,
        long StartLine);

    internal readonly record struct RelationshipLocateRow(
        string RowId,
        string Name,
        long? StartByte,
        long? EndByte,
        long StartLine);
}
