namespace Miller.Core.Resolution;

/// <summary>One explicit directory or module scope in which a QML type is visible.</summary>
public sealed record QmlVisibilityScope
{
    public string? Directory { get; }

    public string? Module { get; }

    public QmlVisibilityScope(string? Directory, string? Module)
    {
        bool hasDirectory = !string.IsNullOrWhiteSpace(Directory);
        bool hasModule = !string.IsNullOrWhiteSpace(Module);
        if (hasDirectory == hasModule)
            throw new ArgumentException("Exactly one QML visibility scope must be provided.");

        this.Directory = hasDirectory ? Directory : null;
        this.Module = hasModule ? Module : null;
    }

    public static QmlVisibilityScope ForDirectory(string directory) => new(directory, null);

    public static QmlVisibilityScope ForModule(string module) => new(null, module);
}

/// <summary>A QML major/minor version used by an import or exported type.</summary>
public sealed record QmlVersion
{
    public int Major { get; }

    public int Minor { get; }

    public QmlVersion(int Major, int Minor)
    {
        if (Major < 0)
            throw new ArgumentOutOfRangeException(nameof(Major));
        if (Minor < 0)
            throw new ArgumentOutOfRangeException(nameof(Minor));

        this.Major = Major;
        this.Minor = Minor;
    }
}

/// <summary>Optional QML version range and revision constraint.</summary>
public sealed record QmlVersionConstraint
{
    public QmlVersion? Minimum { get; }

    public QmlVersion? Maximum { get; }

    public string? Revision { get; }

    public QmlVersionConstraint(
        QmlVersion? Minimum = null,
        QmlVersion? Maximum = null,
        string? Revision = null)
    {
        if (Minimum is { } minimum && Maximum is { } maximum && Compare(minimum, maximum) > 0)
            throw new ArgumentException("QML version minimum cannot exceed maximum.");

        this.Minimum = Minimum;
        this.Maximum = Maximum;
        this.Revision = string.IsNullOrWhiteSpace(Revision) ? null : Revision;
    }

    public bool IsCompatibleWith(QmlVersionConstraint? other)
    {
        if (other is null)
            return true;

        if (Revision is not null && other.Revision is not null
            && !string.Equals(Revision, other.Revision, StringComparison.Ordinal))
            return false;

        QmlVersion? minimum = Minimum is { } leftMinimum && other.Minimum is { } rightMinimum
            ? Max(leftMinimum, rightMinimum)
            : Minimum ?? other.Minimum;
        QmlVersion? maximum = Maximum is { } leftMaximum && other.Maximum is { } rightMaximum
            ? Min(leftMaximum, rightMaximum)
            : Maximum ?? other.Maximum;

        return minimum is null || maximum is null || Compare(minimum, maximum) <= 0;
    }

    private static QmlVersion Max(QmlVersion left, QmlVersion right) => Compare(left, right) >= 0 ? left : right;

    private static QmlVersion Min(QmlVersion left, QmlVersion right) => Compare(left, right) <= 0 ? left : right;

    private static int Compare(QmlVersion left, QmlVersion right)
    {
        int major = left.Major.CompareTo(right.Major);
        return major != 0 ? major : left.Minor.CompareTo(right.Minor);
    }
}

/// <summary>Producer provenance and the byte span that supports one QML fact.</summary>
public sealed record QmlEvidence
{
    public string SourcePath { get; }

    public string Provenance { get; }

    public long StartByte { get; }

    public long EndByte { get; }

    public QmlEvidence(string SourcePath, string Provenance, long StartByte, long EndByte)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Provenance);
        if (StartByte < 0)
            throw new ArgumentOutOfRangeException(nameof(StartByte));
        if (EndByte < StartByte)
            throw new ArgumentOutOfRangeException(nameof(EndByte));

        this.SourcePath = SourcePath;
        this.Provenance = Provenance;
        this.StartByte = StartByte;
        this.EndByte = EndByte;
    }
}

/// <summary>Typed QML visibility evidence consumed by the core resolver.</summary>
public sealed record QmlVisibleType
{
    public long ConsumerVersionId { get; }

    public FactSymbolKey Target { get; }

    public string ExportedName { get; }

    public string SourceComponentPath { get; }

    public QmlVisibilityScope Scope { get; }

    public QmlVersionConstraint? VersionConstraint { get; }

    public string? ImportAlias { get; }

    public bool IsInternal { get; }

    public bool IsSingleton { get; }

    public QmlEvidence Evidence { get; }

    public QmlVisibleType(
        long ConsumerVersionId,
        FactSymbolKey Target,
        string ExportedName,
        string SourceComponentPath,
        QmlVisibilityScope Scope,
        QmlVersionConstraint? VersionConstraint,
        string? ImportAlias,
        bool IsInternal,
        bool IsSingleton,
        QmlEvidence Evidence)
    {
        if (ConsumerVersionId < 0)
            throw new ArgumentOutOfRangeException(nameof(ConsumerVersionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(ExportedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceComponentPath);
        ArgumentNullException.ThrowIfNull(Scope);
        if (ImportAlias is not null && string.IsNullOrWhiteSpace(ImportAlias))
            throw new ArgumentException("QML import aliases must contain text.", nameof(ImportAlias));
        ArgumentNullException.ThrowIfNull(Evidence);

        this.ConsumerVersionId = ConsumerVersionId;
        this.Target = Target;
        this.ExportedName = ExportedName;
        this.SourceComponentPath = SourceComponentPath;
        this.Scope = Scope;
        this.VersionConstraint = VersionConstraint;
        this.ImportAlias = ImportAlias;
        this.IsInternal = IsInternal;
        this.IsSingleton = IsSingleton;
        this.Evidence = Evidence;
    }
}

/// <summary>One QML type use and its explicit import context.</summary>
public sealed record QmlVisibilityRequest
{
    public long ConsumerVersionId { get; }

    public string ConsumerComponentPath { get; }

    public string TypeName { get; }

    public QmlVisibilityScope? ImportScope { get; }

    public QmlVersionConstraint? VersionConstraint { get; }

    public string? ImportAlias { get; }

    public QmlVisibilityRequest(
        long ConsumerVersionId,
        string ConsumerComponentPath,
        string TypeName,
        QmlVisibilityScope? ImportScope = null,
        QmlVersionConstraint? VersionConstraint = null,
        string? ImportAlias = null)
    {
        if (ConsumerVersionId < 0)
            throw new ArgumentOutOfRangeException(nameof(ConsumerVersionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(ConsumerComponentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(TypeName);
        if (ImportAlias is not null && string.IsNullOrWhiteSpace(ImportAlias))
            throw new ArgumentException("QML import aliases must contain text.", nameof(ImportAlias));

        this.ConsumerVersionId = ConsumerVersionId;
        this.ConsumerComponentPath = ConsumerComponentPath;
        this.TypeName = TypeName;
        this.ImportScope = ImportScope;
        this.VersionConstraint = VersionConstraint;
        this.ImportAlias = ImportAlias;
    }
}
