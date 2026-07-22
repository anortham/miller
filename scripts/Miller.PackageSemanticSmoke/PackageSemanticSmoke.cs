using Miller.Indexing.Semantic;
using Microsoft.Data.Sqlite;

namespace Miller.PackageSemanticSmoke;

public sealed record PackageSemanticPayloadPaths(
    string PackageRoot,
    string SidecarPath,
    string SqliteVecPath)
{
    public static PackageSemanticPayloadPaths FromPackageRoot(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        string root = Path.GetFullPath(packageRoot);
        string tools = Path.Combine(root, ".tools");
        string sidecar = Path.Combine(tools, OperatingSystem.IsWindows()
            ? "julie-semantic-sidecar.exe"
            : "julie-semantic-sidecar");
        return new(root, sidecar, Path.Combine(tools, VectorStore.PackagedExtensionFileName));
    }
}

public sealed record VectorSelfQueryResult(long RowId, double Distance);

public sealed record PackageSemanticSmokeResult(bool Succeeded, string Stage, string Message)
{
    public static PackageSemanticSmokeResult Pass(string message) => new(true, "complete", message);

    public static PackageSemanticSmokeResult Fail(string stage, string message) => new(false, stage, message);
}

public interface IPackageSemanticSession : IAsyncDisposable
{
    string? UnavailableReason { get; }

    Task<SemanticEncoderHandshake?> EnsureStartedAsync(CancellationToken cancellationToken);

    Task<SemanticEmbedOutcome> EmbedAsync(string text, CancellationToken cancellationToken);
}

public interface IVectorSelfQuery
{
    VectorSelfQueryResult InsertAndQuery(string extensionPath, float[] vector);
}

public sealed class PackageSemanticSmokeRunner
{
    public const string FixedInput = "How does Miller refresh a workspace index?";

    private readonly Func<string, SemanticEncoderPin, IPackageSemanticSession> _sessionFactory;
    private readonly IVectorSelfQuery _vectorSelfQuery;

    public PackageSemanticSmokeRunner(
        Func<string, SemanticEncoderPin, IPackageSemanticSession> sessionFactory,
        IVectorSelfQuery vectorSelfQuery)
    {
        _sessionFactory = sessionFactory;
        _vectorSelfQuery = vectorSelfQuery;
    }

    public async Task<PackageSemanticSmokeResult> RunAsync(
        PackageSemanticPayloadPaths paths,
        SemanticEncoderPin pin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(pin);

        if (!File.Exists(paths.SidecarPath))
            return PackageSemanticSmokeResult.Fail("sidecar-file", $"staged sidecar is missing: {paths.SidecarPath}");
        if (!File.Exists(paths.SqliteVecPath))
            return PackageSemanticSmokeResult.Fail("sqlite-vec-file", $"staged sqlite-vec is missing: {paths.SqliteVecPath}");

        IPackageSemanticSession session;
        try
        {
            session = _sessionFactory(paths.SidecarPath, pin);
        }
        catch (Exception ex)
        {
            return PackageSemanticSmokeResult.Fail("sidecar-launch", ex.Message);
        }

        await using (session.ConfigureAwait(false))
        {
            SemanticEncoderHandshake? handshake;
            try
            {
                handshake = await session.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return PackageSemanticSmokeResult.Fail("handshake-identity", ex.Message);
            }

            string expectedFingerprint = MillerSemanticContract.EncoderFingerprint(pin);
            if (handshake is null)
            {
                return PackageSemanticSmokeResult.Fail(
                    "handshake-identity",
                    session.UnavailableReason ?? "sidecar did not return a usable handshake");
            }
            if (!string.Equals(handshake.Pin.ModelId, pin.ModelId, StringComparison.Ordinal)
                || !string.Equals(handshake.EncoderFingerprint, expectedFingerprint, StringComparison.Ordinal)
                || handshake.Dims != pin.Dims)
            {
                return PackageSemanticSmokeResult.Fail(
                    "handshake-identity",
                    $"expected {pin.ModelId}/{expectedFingerprint}/{pin.Dims}, got " +
                    $"{handshake.Pin.ModelId}/{handshake.EncoderFingerprint}/{handshake.Dims}");
            }

            SemanticEmbedOutcome outcome;
            try
            {
                outcome = await session.EmbedAsync(FixedInput, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return PackageSemanticSmokeResult.Fail("embedding", ex.Message);
            }
            if (!outcome.Succeeded)
                return PackageSemanticSmokeResult.Fail("embedding", outcome.FailureReason ?? "embedding failed");
            if (outcome.Vectors.Count != 1)
            {
                return PackageSemanticSmokeResult.Fail(
                    "embedding",
                    $"expected one embedding, got {outcome.Vectors.Count}");
            }

            float[] embedding = outcome.Vectors[0];
            if (embedding.Length != pin.Dims)
            {
                return PackageSemanticSmokeResult.Fail(
                    "embedding-dimension",
                    $"expected {pin.Dims} dimensions, got {embedding.Length}");
            }

            VectorSelfQueryResult self;
            try
            {
                self = _vectorSelfQuery.InsertAndQuery(paths.SqliteVecPath, embedding);
            }
            catch (Exception ex)
            {
                return PackageSemanticSmokeResult.Fail("sqlite-vec-load", ex.Message);
            }

            if (self.RowId != 1 || !double.IsFinite(self.Distance) || Math.Abs(self.Distance) > 1e-4)
            {
                return PackageSemanticSmokeResult.Fail(
                    "knn-self-query",
                    $"expected rowid 1 at near-zero distance, got rowid {self.RowId} at {self.Distance:R}");
            }

            return PackageSemanticSmokeResult.Pass(
                $"{pin.ModelId}/{pin.Dims} embedded and returned rowid 1 at distance {self.Distance:R}");
        }
    }
}

public sealed class ProcessPackageSemanticSession : IPackageSemanticSession
{
    private readonly SemanticEmbeddingSession _session;

    public ProcessPackageSemanticSession(string executable, SemanticEncoderPin pin)
    {
        _session = new SemanticEmbeddingSession(
            ProcessSemanticSidecarLauncher.ForServe(executable, pin),
            expectedEncoder: pin);
    }

    public string? UnavailableReason => _session.UnavailableReason;

    public Task<SemanticEncoderHandshake?> EnsureStartedAsync(CancellationToken cancellationToken) =>
        _session.EnsureStartedAsync(cancellationToken);

    public Task<SemanticEmbedOutcome> EmbedAsync(string text, CancellationToken cancellationToken) =>
        _session.EmbedQueryAsync(text, cancellationToken);

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

public sealed class SqliteVecSelfQuery : IVectorSelfQuery
{
    public VectorSelfQueryResult InsertAndQuery(string extensionPath, float[] vector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionPath);
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length == 0)
            throw new ArgumentException("embedding must not be empty", nameof(vector));

        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        connection.EnableExtensions(true);
        connection.LoadExtension(Path.GetFullPath(extensionPath));
        connection.EnableExtensions(false);

        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText =
                $"CREATE VIRTUAL TABLE smoke_vectors USING vec0(embedding float[{vector.Length}] distance_metric=cosine)";
            create.ExecuteNonQuery();
        }

        byte[] blob = ToBlob(vector);
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO smoke_vectors(rowid, embedding) VALUES (1, @embedding)";
            insert.Parameters.AddWithValue("@embedding", blob);
            insert.ExecuteNonQuery();
        }

        using SqliteCommand query = connection.CreateCommand();
        query.CommandText =
            "SELECT rowid, distance FROM smoke_vectors " +
            "WHERE embedding MATCH @query AND k = 1 ORDER BY distance";
        query.Parameters.AddWithValue("@query", blob);
        using SqliteDataReader reader = query.ExecuteReader();
        return reader.Read()
            ? new VectorSelfQueryResult(reader.GetInt64(0), reader.GetDouble(1))
            : new VectorSelfQueryResult(-1, double.PositiveInfinity);
    }

    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
