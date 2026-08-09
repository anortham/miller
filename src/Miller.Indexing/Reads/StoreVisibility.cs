namespace Miller.Indexing.Reads;

public sealed record StoreVisibility(
    string FamilyId,
    string StoreRoot,
    string GenerationName,
    string StoreDatabasePath,
    string CoordinatorDatabasePath,
    string ViewId,
    string WorkspaceRoot,
    long ManifestGeneration,
    string ManifestHash,
    string ResolutionState,
    string? ResolutionBaseId,
    long? ResolutionDeltaGeneration,
    long? ResolutionExactAt,
    long StoreLogSequence,
    string IndexLevel,
    string StoreInstanceId,
    string LevelStampL1,
    string LevelStampL2,
    string LevelStampL3);
