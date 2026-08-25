using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Core.Resolution;
using Miller.Indexing.Reads;

namespace Miller.Indexing.Resolution;

internal sealed class QmlVisibilityCatalog
{
    private readonly Dictionary<long, IReadOnlyList<QmlVisibleType>>? _candidates;
    private readonly Func<long, QmlVisibleType[]>? _load;
    private readonly Dictionary<long, IReadOnlyList<QmlVisibleType>> _loaded = [];
    private readonly object _gate = new();
    private int _boundedStructuralFactRowsRead;

    private QmlVisibilityCatalog(Dictionary<long, QmlVisibleType[]> candidates)
    {
        _candidates = candidates.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<QmlVisibleType>)Array.AsReadOnly(pair.Value));
    }

    private QmlVisibilityCatalog(Func<long, QmlVisibleType[]> load)
    {
        _load = load;
    }

    internal IReadOnlyList<QmlVisibleType> For(long consumerVersionId)
    {
        if (_candidates is not null)
            return _candidates.TryGetValue(consumerVersionId, out IReadOnlyList<QmlVisibleType>? values) ? values : [];

        lock (_gate)
        {
            if (_loaded.TryGetValue(consumerVersionId, out IReadOnlyList<QmlVisibleType>? values))
                return values;
            values = Array.AsReadOnly(_load!(consumerVersionId));
            _loaded[consumerVersionId] = values;
            return values;
        }
    }

    internal int BoundedStructuralFactRowsRead => Volatile.Read(ref _boundedStructuralFactRowsRead);

    internal static QmlVisibilityCatalog LoadStore(
        SqliteConnection connection,
        StoreVisibility visibility,
        IReadOnlyList<RevisionFactCacheLoader.VisibleFile> files,
        StringInternPool intern)
    {
        List<QmlStructuralRow> facts = ReadStoreFacts(connection, visibility, intern);
        List<QmlSymbolRow> symbols = ReadStoreSymbols(connection, visibility, files.Select(file => file.VersionId).ToArray(), intern);
        return new QmlVisibilityCatalog(BuildConsumers(files, symbols, facts, intern));
    }

    internal static QmlVisibilityCatalog LoadArtifact(
        SqliteConnection connection,
        IReadOnlyList<RevisionFactCacheLoader.VisibleFile> files,
        StringInternPool intern)
    {
        List<QmlStructuralRow> facts = ReadArtifactFacts(connection, intern);
        List<QmlSymbolRow> symbols = ReadArtifactSymbols(connection, intern);
        return new QmlVisibilityCatalog(BuildConsumers(files, symbols, facts, intern));
    }

    internal static QmlVisibilityCatalog LoadBoundedStore(
        SqliteConnection connection,
        StoreVisibility visibility,
        IReadOnlyList<RevisionFactCacheLoader.VisibleFile> files,
        StringInternPool intern)
    {
        QmlVisibilityCatalog? catalog = null;
        catalog = new QmlVisibilityCatalog(versionId =>
        {
            if (!files.Any(file =>
                    file.VersionId == versionId
                    && string.Equals(file.Language, "qml", StringComparison.OrdinalIgnoreCase)
                    && file.Path.EndsWith(".qml", StringComparison.OrdinalIgnoreCase)))
                return [];

            RevisionFactCacheLoader.VisibleFile consumer = files.Last(file => file.VersionId == versionId);
            List<QmlSymbolRow> consumerSymbols = ReadStoreSymbols(connection, visibility, [versionId], intern);
            List<QmlImportRow> imports = ParseImports(consumerSymbols, consumer.Path, intern);
            List<QmlStructuralRow> facts = ReadStoreFactsForConsumer(connection, visibility, consumer.Path, imports, intern);
            Volatile.Write(ref catalog!._boundedStructuralFactRowsRead, facts.Count);
            Dictionary<long, QmlStructuralModel> models = DecodeFacts(facts, intern);
            HashSet<string> paths = RelevantPaths(consumer.Path, imports, files, models);
            HashSet<long> versions = files
                .Where(file => paths.Contains(NormalizePath(file.Path)))
                .Select(file => file.VersionId)
                .ToHashSet();
            List<QmlSymbolRow> symbols = ReadStoreSymbols(connection, visibility, versions, intern);
            return BuildConsumer(consumer, imports, symbols, models, intern);
        });
        return catalog;
    }

    private static Dictionary<long, QmlVisibleType[]> BuildConsumers(
        IReadOnlyList<RevisionFactCacheLoader.VisibleFile> files,
        IReadOnlyList<QmlSymbolRow> symbols,
        IReadOnlyList<QmlStructuralRow> facts,
        StringInternPool intern)
    {
        Dictionary<long, QmlStructuralModel> models = DecodeFacts(facts, intern);
        var result = new Dictionary<long, QmlVisibleType[]>();
        foreach (RevisionFactCacheLoader.VisibleFile file in files)
        {
            if (!string.Equals(file.Language, "qml", StringComparison.OrdinalIgnoreCase)
                || !file.Path.EndsWith(".qml", StringComparison.OrdinalIgnoreCase))
                continue;

            List<QmlImportRow> imports = ParseImports(
                symbols.Where(symbol => symbol.VersionId == file.VersionId),
                file.Path,
                intern);
            result[file.VersionId] = BuildConsumer(
                file,
                imports,
                symbols,
                models,
                intern);
        }

        return result;
    }

    private static List<QmlImportRow> ParseImports(
        IEnumerable<QmlSymbolRow> symbols,
        string path,
        StringInternPool intern)
    {
        var imports = new List<QmlImportRow>();
        foreach (QmlSymbolRow symbol in symbols.Where(symbol => symbol.Kind == FactSymbolKind.Import))
        {
            if (symbol.MetadataJson is null)
                continue;
            if (!TryReadObject(symbol.MetadataJson, out JsonElement root))
                continue;
            string? kind = ReadString(root, "import_kind");
            if (kind is not ("directory" or "module"))
                continue;
            string? source = FirstNonEmpty(ReadString(root, "source"), ReadString(root, "import_module"));
            if (source is null)
                continue;
            if (!TryParseVersionConstraint(root, out QmlVersionConstraint? version))
                continue;
            string? alias = FirstNonEmpty(ReadString(root, "alias"), ReadString(root, "local_name"));
            imports.Add(new QmlImportRow(
                intern.Intern(NormalizePath(path)),
                intern.Intern(kind),
                intern.Intern(source),
                alias is null ? null : intern.Intern(alias),
                version,
                symbol.StartByte,
                symbol.EndByte));
        }

        return imports;
    }

    private static QmlVisibleType[] BuildConsumer(
        RevisionFactCacheLoader.VisibleFile consumer,
        IReadOnlyList<QmlImportRow> imports,
        IReadOnlyList<QmlSymbolRow> symbols,
        IReadOnlyDictionary<long, QmlStructuralModel> models,
        StringInternPool intern)
    {
        if (!string.Equals(consumer.Language, "qml", StringComparison.OrdinalIgnoreCase))
            return [];

        Dictionary<(string Path, string Name), QmlSymbolRow> targets = symbols
            .Where(IsQmlComponent)
            .GroupBy(symbol => (NormalizePath(symbol.Path), symbol.Name))
            .ToDictionary(group => group.Key, group => group.OrderBy(symbol => symbol.SymbolId, StringComparer.Ordinal).First());
        Dictionary<string, QmlSymbolRow[]> targetsByPath = targets
            .GroupBy(pair => pair.Key.Path, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(pair => pair.Value)
                    .OrderBy(symbol => symbol.SymbolId, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        Dictionary<string, QmlManifest> manifests = BuildManifests(symbols, models, intern);
        Dictionary<string, QmlManifestEntry[]> entries = manifests
            .SelectMany(pair => pair.Value.Entries.Select(entry =>
                (Path: NormalizePath(Combine(DirectoryOf(pair.Key), entry.File)), Entry: entry)))
            .GroupBy(pair => pair.Path, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(pair => pair.Entry)
                    .OrderBy(entry => entry.TypeName, StringComparer.Ordinal)
                    .ThenBy(entry => entry.StartByte)
                    .ToArray(),
                StringComparer.Ordinal);
        var candidates = new List<QmlVisibleType>();
        string consumerPath = NormalizePath(consumer.Path);
        string consumerDirectory = DirectoryOf(consumerPath);

        foreach (KeyValuePair<(string Path, string Name), QmlSymbolRow> pair in targets)
        {
            (string Path, string Name) key = pair.Key;
            QmlSymbolRow target = pair.Value;
            if (!string.Equals(DirectoryOf(key.Path), consumerDirectory, StringComparison.Ordinal))
                continue;
            QmlManifestEntry? entry = entries.TryGetValue(key.Path, out QmlManifestEntry[]? pathEntries)
                ? pathEntries.FirstOrDefault(candidate => string.Equals(candidate.TypeName, target.Name, StringComparison.Ordinal))
                : null;
            candidates.Add(CreateCandidate(
                consumer.VersionId,
                target,
                entry,
                QmlVisibilityScope.ForDirectory(consumerDirectory),
                importAlias: null,
                new QmlEvidence(
                    intern.Intern(NormalizePath(target.Path)),
                    "qml.component",
                    target.StartByte,
                    target.EndByte),
                intern));
        }

        foreach (QmlImportRow import in imports)
        {
            if (string.Equals(import.Kind, "directory", StringComparison.Ordinal))
            {
                string directory = ResolveDirectory(consumerPath, import.Source);
                foreach (QmlManifest manifest in manifests.Values)
                {
                    if (!string.Equals(DirectoryOf(manifest.Path), directory, StringComparison.Ordinal))
                        continue;
                    foreach (QmlManifestEntry entry in manifest.Entries)
                    {
                        string targetPath = NormalizePath(Combine(DirectoryOf(manifest.Path), entry.File));
                        if (!targetsByPath.TryGetValue(targetPath, out QmlSymbolRow[]? pathTargets))
                            continue;
                        foreach (QmlSymbolRow target in pathTargets)
                        {
                            if (entry.IsInternal && !string.Equals(consumerDirectory, directory, StringComparison.Ordinal))
                                continue;
                            candidates.Add(CreateCandidate(
                                consumer.VersionId,
                                target,
                                entry,
                                QmlVisibilityScope.ForDirectory(directory),
                                import.Alias,
                                Evidence(entry, manifest.Path, intern),
                                intern));
                        }
                    }
                }

                if (!manifests.Values.Any(manifest =>
                        string.Equals(DirectoryOf(manifest.Path), directory, StringComparison.Ordinal)))
                {
                    foreach (QmlSymbolRow target in targets.Values)
                    {
                        string targetPath = NormalizePath(target.Path);
                        if (!string.Equals(DirectoryOf(targetPath), directory, StringComparison.Ordinal))
                            continue;
                        candidates.Add(CreateCandidate(
                            consumer.VersionId,
                            target,
                            entry: null,
                            QmlVisibilityScope.ForDirectory(directory),
                            import.Alias,
                            new QmlEvidence(
                                import.Path,
                                "qml.import",
                                import.StartByte,
                                import.EndByte),
                            intern));
                    }
                }
            }
            else
            {
                foreach (QmlManifest manifest in manifests.Values)
                {
                    if (!string.Equals(manifest.Module, import.Source, StringComparison.Ordinal))
                        continue;
                    foreach (QmlManifestEntry entry in manifest.Entries)
                    {
                        if (entry.IsInternal
                            || import.Version is not null
                            && entry.VersionConstraint is not null
                            && !entry.VersionConstraint.IsCompatibleWith(import.Version))
                            continue;
                        string targetPath = NormalizePath(Combine(DirectoryOf(manifest.Path), entry.File));
                        if (!targetsByPath.TryGetValue(targetPath, out QmlSymbolRow[]? pathTargets)
                            || !manifest.TypeInfoAllows(entry.TypeName, entry.VersionConstraint))
                            continue;
                        foreach (QmlSymbolRow target in pathTargets)
                        {
                            candidates.Add(CreateCandidate(
                                consumer.VersionId,
                                target,
                                entry,
                                QmlVisibilityScope.ForModule(manifest.Module),
                                import.Alias,
                                Evidence(entry, manifest.Path, intern),
                                intern));
                        }
                    }
                }
            }
        }

        return candidates
            .Distinct()
            .OrderBy(candidate => candidate.ExportedName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ImportAlias ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Target.SymbolId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Scope.Directory ?? candidate.Scope.Module, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsQmlComponent(QmlSymbolRow symbol) =>
        symbol.Kind == FactSymbolKind.Class
        && string.Equals(symbol.Language, "qml", StringComparison.OrdinalIgnoreCase)
        && symbol.Path.EndsWith(".qml", StringComparison.OrdinalIgnoreCase);

    private static QmlVisibleType CreateCandidate(
        long consumerVersionId,
        QmlSymbolRow target,
        QmlManifestEntry? entry,
        QmlVisibilityScope scope,
        string? importAlias,
        QmlEvidence evidence,
        StringInternPool intern)
    {
        return new QmlVisibleType(
            consumerVersionId,
            new FactSymbolKey(target.VersionId, target.SymbolId),
            intern.Intern(entry?.TypeName ?? target.Name),
            intern.Intern(NormalizePath(target.Path)),
            scope,
            entry?.VersionConstraint,
            importAlias,
            entry?.IsInternal == true,
            entry?.IsSingleton == true,
            evidence);
    }

    private static QmlEvidence Evidence(QmlManifestEntry entry, string manifestPath, StringInternPool intern) =>
        new(
            intern.Intern(NormalizePath(manifestPath)),
            "qmldir",
            entry.StartByte,
            entry.EndByte);

    private static Dictionary<string, QmlManifest> BuildManifests(
        IReadOnlyList<QmlSymbolRow> symbols,
        IReadOnlyDictionary<long, QmlStructuralModel> models,
        StringInternPool intern)
    {
        var manifests = new Dictionary<string, QmlManifest>(StringComparer.Ordinal);
        foreach (QmlStructuralModel model in models.Values)
        {
            if (model.Module is null)
                continue;
            var manifest = new QmlManifest(model.Path, model.Module, model.TypeInfoFile);
            foreach (QmlManifestFact fact in model.Entries)
            {
                manifest.Entries.Add(new QmlManifestEntry(
                    intern.Intern(fact.TypeName),
                    intern.Intern(fact.File),
                    fact.Version,
                    fact.IsInternal,
                    fact.IsSingleton,
                    fact.StartByte,
                    fact.EndByte));
            }

            if (manifest.TypeInfoFile is not null)
            {
                string typeInfoPath = NormalizePath(Combine(DirectoryOf(model.Path), manifest.TypeInfoFile));
                QmlStructuralModel? typeInfoModel = models.Values.FirstOrDefault(candidate =>
                    string.Equals(
                        NormalizePath(candidate.Path),
                        typeInfoPath,
                        StringComparison.Ordinal));
                if (typeInfoModel is not null)
                {
                    foreach (QmlSymbolRow symbol in symbols)
                    {
                        if (!string.Equals(NormalizePath(symbol.Path), typeInfoPath, StringComparison.Ordinal)
                            || symbol.Kind != FactSymbolKind.Class
                            || !TryReadObject(symbol.MetadataJson, out JsonElement root)
                            || !string.Equals(ReadString(root, "typeinfo_kind"), "type", StringComparison.Ordinal))
                            continue;
                        QmlTypeInfo? info = ParseTypeInfo(symbol, typeInfoModel, intern);
                        if (info is not null)
                            manifest.TypeInfos.Add(info);
                    }
                }
            }

            manifests[NormalizePath(model.Path)] = manifest;
        }

        return manifests;
    }

    private static QmlTypeInfo? ParseTypeInfo(QmlSymbolRow symbol, QmlStructuralModel model, StringInternPool intern)
    {
        if (!model.TypeInfoNames.Contains(symbol.Name)
            || !TryReadObject(symbol.MetadataJson, out JsonElement root)
            || !root.TryGetProperty("exports", out JsonElement exports)
            || exports.ValueKind != JsonValueKind.Array)
            return null;
        var values = new List<(string Module, QmlVersion Version)>();
        foreach (JsonElement export in exports.EnumerateArray())
        {
            if (export.ValueKind != JsonValueKind.String
                || !TryParseExport(export.GetString(), out string module, out QmlVersion? version)
                || version is null)
                continue;
            values.Add((intern.Intern(module), version));
        }

        return values.Count == 0 ? null : new QmlTypeInfo(symbol.Name, values);
    }

    private static bool TryParseExport(string? value, out string module, out QmlVersion? version)
    {
        module = string.Empty;
        version = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string[] parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !TryParseVersion(parts[1], out version))
            return false;
        module = parts[0].Replace("/", ".", StringComparison.Ordinal);
        return module.Length > 0;
    }

    private static HashSet<string> RelevantPaths(
        string consumerPath,
        IReadOnlyList<QmlImportRow> imports,
        IReadOnlyList<RevisionFactCacheLoader.VisibleFile> files,
        IReadOnlyDictionary<long, QmlStructuralModel> models)
    {
        string consumerDirectory = DirectoryOf(consumerPath);
        var directories = new HashSet<string>(StringComparer.Ordinal) { consumerDirectory };
        foreach (QmlImportRow import in imports.Where(import => import.Kind == "directory"))
            directories.Add(ResolveDirectory(consumerPath, import.Source));
        foreach (QmlImportRow import in imports.Where(import => import.Kind == "module"))
        {
            foreach (QmlStructuralModel model in models.Values.Where(model => string.Equals(model.Module, import.Source, StringComparison.Ordinal)))
                directories.Add(DirectoryOf(model.Path));
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (RevisionFactCacheLoader.VisibleFile file in files)
        {
            string path = NormalizePath(file.Path);
            if (directories.Contains(DirectoryOf(path)))
                paths.Add(path);
        }

        foreach (QmlStructuralModel model in models.Values)
        {
            if (directories.Contains(DirectoryOf(model.Path)))
                paths.Add(NormalizePath(model.Path));
            if (model.TypeInfoFile is not null && directories.Contains(DirectoryOf(model.Path)))
                paths.Add(NormalizePath(Combine(DirectoryOf(model.Path), model.TypeInfoFile)));
        }

        return paths;
    }

    private static List<QmlSymbolRow> ReadStoreSymbols(
        SqliteConnection connection,
        StoreVisibility visibility,
        IReadOnlyCollection<long> versions,
        StringInternPool intern)
    {
        if (versions.Count == 0)
            return [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT s.version_id,s.path,s.symbol_id,s.name,s.kind,s.language,s.visibility,
                   s.start_byte,s.end_byte,s.metadata_json
            FROM main.symbols AS s
            JOIN main.manifest_entries AS e ON e.version_id=s.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
              AND (s.language='qml' OR s.language='qmldir')
              AND EXISTS (
                    SELECT 1 FROM json_each($versions) requested
                    WHERE CAST(requested.value AS INTEGER)=s.version_id)
            ORDER BY s.version_id,s.symbol_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$versions", JsonSerializer.Serialize(versions));
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<QmlSymbolRow>();
        while (reader.Read())
        {
            FactSymbolKind? kind = ResolutionPolicy.ParseSymbolKind(reader.GetString(4));
            if (kind is null)
                continue;
            rows.Add(new QmlSymbolRow(
                reader.GetInt64(0),
                intern.Intern(NormalizePath(reader.GetString(1))),
                intern.Intern(reader.GetString(2)),
                intern.Intern(reader.GetString(3)),
                kind.Value,
                intern.Intern(reader.GetString(5)),
                reader.IsDBNull(6) ? null : intern.Intern(reader.GetString(6)),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return rows;
    }

    private static List<QmlSymbolRow> ReadArtifactSymbols(SqliteConnection connection, StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.rowid,s.path,s.symbol_id,s.name,s.kind,s.language,s.visibility,
                   s.start_byte,s.end_byte,s.metadata_json
            FROM symbols AS s
            JOIN files AS f ON f.file_id=s.file_id
            WHERE s.language='qml' OR s.language='qmldir'
            ORDER BY f.rowid,s.symbol_id
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<QmlSymbolRow>();
        while (reader.Read())
        {
            FactSymbolKind? kind = ResolutionPolicy.ParseSymbolKind(reader.GetString(4));
            if (kind is null)
                continue;
            rows.Add(new QmlSymbolRow(
                reader.GetInt64(0),
                intern.Intern(NormalizePath(reader.GetString(1))),
                intern.Intern(reader.GetString(2)),
                intern.Intern(reader.GetString(3)),
                kind.Value,
                intern.Intern(reader.GetString(5)),
                reader.IsDBNull(6) ? null : intern.Intern(reader.GetString(6)),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return rows;
    }

    private static List<QmlStructuralRow> ReadStoreFacts(
        SqliteConnection connection,
        StoreVisibility visibility,
        StringInternPool intern)
    {
        if (!TableExists(connection, "structural_facts"))
            return [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT f.version_id,f.path,f.pattern_id,f.start_byte,f.end_byte,f.metadata_json
            FROM main.structural_facts AS f
            JOIN main.manifest_entries AS e ON e.version_id=f.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
            ORDER BY f.version_id,f.path,f.start_byte,f.pattern_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        return ReadFacts(command, intern);
    }

    private static List<QmlStructuralRow> ReadStoreFactsForConsumer(
        SqliteConnection connection,
        StoreVisibility visibility,
        string consumerPath,
        IReadOnlyList<QmlImportRow> imports,
        StringInternPool intern)
    {
        if (!TableExists(connection, "structural_facts"))
            return [];

        string consumerDirectory = DirectoryOf(consumerPath);
        var directories = new HashSet<string>(StringComparer.Ordinal) { consumerDirectory };
        foreach (QmlImportRow import in imports.Where(import => import.Kind == "directory"))
            directories.Add(ResolveDirectory(consumerPath, import.Source));

        string[] modules = imports
            .Where(import => import.Kind == "module")
            .Select(import => import.Source)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        List<QmlStructuralRow> moduleFacts = ReadStoreModuleFacts(
            connection,
            visibility,
            modules,
            directories,
            intern);
        Dictionary<long, QmlStructuralModel> moduleModels = DecodeFacts(moduleFacts, intern);
        foreach (QmlStructuralModel model in moduleModels.Values)
            directories.Add(DirectoryOf(model.Path));

        return ReadStoreFactsInDirectories(connection, visibility, directories, intern);
    }

    private static List<QmlStructuralRow> ReadStoreModuleFacts(
        SqliteConnection connection,
        StoreVisibility visibility,
        IReadOnlyCollection<string> modules,
        IReadOnlyCollection<string> directories,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT f.version_id,f.path,f.pattern_id,f.start_byte,f.end_byte,f.metadata_json
            FROM main.structural_facts AS f
            JOIN main.manifest_entries AS e ON e.version_id=f.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
              AND f.pattern_id='qmldir.module.v1'
              AND (
                    EXISTS (
                        SELECT 1 FROM json_each($modules) requested
                        WHERE json_extract(f.metadata_json,'$.module')=requested.value)
                 OR EXISTS (
                        SELECT 1 FROM json_each($directories) requested
                        WHERE (
                            (requested.value='.' AND instr(replace(f.path,'\','/'),'/')=0)
                            OR replace(f.path,'\','/') LIKE requested.value || '/%')))
            ORDER BY f.version_id,f.path,f.start_byte,f.pattern_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$modules", JsonSerializer.Serialize(modules));
        command.Parameters.AddWithValue("$directories", JsonSerializer.Serialize(directories));
        return ReadFacts(command, intern);
    }

    private static List<QmlStructuralRow> ReadStoreFactsInDirectories(
        SqliteConnection connection,
        StoreVisibility visibility,
        IReadOnlyCollection<string> directories,
        StringInternPool intern)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText =
            """
            SELECT f.version_id,f.path,f.pattern_id,f.start_byte,f.end_byte,f.metadata_json
            FROM main.structural_facts AS f
            JOIN main.manifest_entries AS e ON e.version_id=f.version_id
            WHERE e.view_id=$view_id AND e.generation=$generation
              AND EXISTS (
                    SELECT 1 FROM json_each($directories) requested
                    WHERE (
                        (requested.value='.' AND instr(replace(f.path,'\','/'),'/')=0)
                        OR replace(f.path,'\','/') LIKE requested.value || '/%'))
            ORDER BY f.version_id,f.path,f.start_byte,f.pattern_id
            """;
        command.Parameters.AddWithValue("$view_id", visibility.ViewId);
        command.Parameters.AddWithValue("$generation", visibility.ManifestGeneration);
        command.Parameters.AddWithValue("$directories", JsonSerializer.Serialize(directories));
        return ReadFacts(command, intern);
    }

    private static List<QmlStructuralRow> ReadArtifactFacts(SqliteConnection connection, StringInternPool intern)
    {
        if (!TableExists(connection, "structural_facts"))
            return [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.rowid,sf.path,sf.pattern_id,sf.start_byte,sf.end_byte,sf.metadata_json
            FROM structural_facts AS sf
            JOIN files AS f ON f.file_id=sf.file_id
            ORDER BY f.rowid,sf.path,sf.start_byte,sf.pattern_id
            """;
        return ReadFacts(command, intern);
    }

    private static List<QmlStructuralRow> ReadFacts(SqliteCommand command, StringInternPool intern)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<QmlStructuralRow>();
        while (reader.Read())
        {
            rows.Add(new QmlStructuralRow(
                reader.GetInt64(0),
                intern.Intern(NormalizePath(reader.GetString(1))),
                intern.Intern(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    private static Dictionary<long, QmlStructuralModel> DecodeFacts(
        IReadOnlyList<QmlStructuralRow> rows,
        StringInternPool intern)
    {
        var models = new Dictionary<long, QmlStructuralModel>();
        foreach (QmlStructuralRow row in rows)
        {
            if (!TryReadObject(row.MetadataJson, out JsonElement root)
                || !IsVersionOne(root))
                continue;
            if (!models.TryGetValue(row.VersionId, out QmlStructuralModel? model))
            {
                model = new QmlStructuralModel(row.VersionId, row.Path);
                models[row.VersionId] = model;
            }

            switch (row.PatternId)
            {
                case "qmldir.module.v1":
                    if (string.Equals(ReadString(root, "directive"), "module", StringComparison.Ordinal))
                        model.Module = ReadString(root, "module");
                    break;
                case "qmldir.typeinfo.v1":
                    if (string.Equals(ReadString(root, "directive"), "typeinfo", StringComparison.Ordinal))
                        model.TypeInfoFile = ReadString(root, "file");
                    break;
                case "qmldir.object_type.v1":
                    AddManifestFact(model, row, root, isInternal: false, isSingleton: false, "object_type");
                    break;
                case "qmldir.singleton_type.v1":
                    AddManifestFact(model, row, root, isInternal: false, isSingleton: true, "singleton");
                    break;
                case "qmldir.internal_type.v1":
                    AddManifestFact(model, row, root, isInternal: true, isSingleton: false, "internal");
                    break;
                case "qml.typeinfo_declaration.v1":
                    if (string.Equals(ReadString(root, "typeinfo_kind"), "type", StringComparison.Ordinal))
                    {
                        string? name = ReadString(root, "type_name");
                        if (name is not null)
                            model.TypeInfoNames.Add(intern.Intern(name));
                    }
                    break;
            }
        }

        return models;
    }

    private static void AddManifestFact(
        QmlStructuralModel model,
        QmlStructuralRow row,
        JsonElement root,
        bool isInternal,
        bool isSingleton,
        string directive)
    {
        if (!string.Equals(ReadString(root, "directive"), directive, StringComparison.Ordinal))
            return;
        string? file = ReadString(root, "file");
        string? typeName = ReadString(root, "type_name");
        if (file is null || typeName is null)
            return;
        if (!TryParseVersionConstraint(root, out QmlVersionConstraint? version))
            return;
        model.Entries.Add(new QmlManifestFact(typeName, file, version, isInternal, isSingleton, row.StartByte, row.EndByte));
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static bool TryParseVersionConstraint(JsonElement root, out QmlVersionConstraint? constraint)
    {
        constraint = null;
        if (!root.TryGetProperty("version", out JsonElement property))
            return true;
        if (property.ValueKind != JsonValueKind.String
            || !TryParseVersion(property.GetString(), out QmlVersion? version)
            || version is null)
            return false;
        constraint = new QmlVersionConstraint(version, version);
        return true;
    }

    private static bool TryParseVersion(string? raw, out QmlVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        string[] parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int major)
            || !int.TryParse(parts[1], out int minor)
            || major < 0
            || minor < 0)
            return false;
        version = new QmlVersion(major, minor);
        return true;
    }

    private static bool IsVersionOne(JsonElement root) =>
        root.TryGetProperty("pattern_version", out JsonElement version)
        && version.ValueKind == JsonValueKind.Number
        && version.TryGetInt32(out int value)
        && value == 1;

    private static bool TryReadObject(string? json, out JsonElement root)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            root = default;
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                root = default;
                return false;
            }

            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            return null;
        string? value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string ResolveDirectory(string importingPath, string source) =>
        NormalizePath(Combine(DirectoryOf(importingPath), source));

    private static string Combine(string directory, string path)
    {
        if (path.StartsWith("/", StringComparison.Ordinal))
            return path[1..];
        return string.IsNullOrEmpty(directory) || directory == "." ? path : $"{directory}/{path}";
    }

    private static string DirectoryOf(string path)
    {
        string normalized = NormalizePath(path);
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? "." : normalized[..slash];
    }

    private static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (string part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return parts.Count == 0 ? "." : string.Join('/', parts);
    }

    private sealed class QmlStructuralModel
    {
        internal QmlStructuralModel(long versionId, string path)
        {
            VersionId = versionId;
            Path = path;
        }

        internal long VersionId { get; }
        internal string Path { get; }
        internal string? Module { get; set; }
        internal string? TypeInfoFile { get; set; }
        internal HashSet<string> TypeInfoNames { get; } = new(StringComparer.Ordinal);
        internal List<QmlManifestFact> Entries { get; } = [];
    }

    private sealed class QmlManifest
    {
        internal QmlManifest(string path, string module, string? typeInfoFile)
        {
            Path = path;
            Module = module;
            TypeInfoFile = typeInfoFile;
        }

        internal string Path { get; }
        internal string Module { get; }
        internal string? TypeInfoFile { get; }
        internal List<QmlManifestEntry> Entries { get; } = [];
        internal List<QmlTypeInfo> TypeInfos { get; } = [];

        internal bool TypeInfoAllows(string typeName, QmlVersionConstraint? version)
        {
            if (TypeInfos.Count == 0)
                return true;
            QmlTypeInfo? info = TypeInfos.FirstOrDefault(type => string.Equals(type.Name, typeName, StringComparison.Ordinal));
            if (info is null)
                return true;
            return info.Exports.Any(export =>
                string.Equals(export.Module, Module, StringComparison.Ordinal)
                && (version is null || export.Version.Major == version.Minimum?.Major && export.Version.Minor == version.Minimum?.Minor));
        }
    }

    private sealed record QmlTypeInfo(string Name, List<(string Module, QmlVersion Version)> Exports);

    private sealed record QmlManifestFact(
        string TypeName,
        string File,
        QmlVersionConstraint? Version,
        bool IsInternal,
        bool IsSingleton,
        long StartByte,
        long EndByte);

    private sealed record QmlManifestEntry(
        string TypeName,
        string File,
        QmlVersionConstraint? VersionConstraint,
        bool IsInternal,
        bool IsSingleton,
        long StartByte,
        long EndByte);

    private sealed record QmlImportRow(
        string Path,
        string Kind,
        string Source,
        string? Alias,
        QmlVersionConstraint? Version,
        long StartByte,
        long EndByte);

    private sealed record QmlSymbolRow(
        long VersionId,
        string Path,
        string SymbolId,
        string Name,
        FactSymbolKind Kind,
        string Language,
        string? Visibility,
        long StartByte,
        long EndByte,
        string? MetadataJson);

    private sealed record QmlStructuralRow(
        long VersionId,
        string Path,
        string PatternId,
        long StartByte,
        long EndByte,
        string? MetadataJson);
}
