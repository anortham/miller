namespace Miller.Indexing;

public sealed record StoreFamilyRegistryRow(
    Guid FamilyId,
    string LineageKey,
    string? CanonicalCommonDir,
    DateTimeOffset? CommonDirCreatedAtUtc,
    string StoreRoot,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record StoreMemberRegistryRow(
    string WorkspaceId,
    Guid FamilyId,
    string ViewId,
    string WorkspaceRoot,
    string? RootGitDir,
    DateTimeOffset? RootGitDirCreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
