namespace Miller.Core.Search;

/// <summary>
/// A free-text document presented to the content index (phase 3). <see cref="DocId"/> is
/// caller-assigned and opaque to the index (NOT assumed to equal the input position).
/// <see cref="Path"/> is the workspace-relative file path carried through to results;
/// <see cref="Text"/> is the full UTF-8 file content (already freshness-verified by the loader).
/// </summary>
public sealed record ContentDocument(int DocId, string Path, string Text, string Language = "");
