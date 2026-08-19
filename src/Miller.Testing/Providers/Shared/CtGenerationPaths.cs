using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Miller.Testing;

/// <summary>
/// Filesystem handle for one immutable CT build generation. Directory names are short hashes so
/// Windows MAX_PATH still has headroom; uniqueness is the incrementing ordinal written into the
/// allocation marker.
/// </summary>
public sealed record CtGenerationPaths(
    string GenerationId,
    string GenerationRoot,
    string OutDir,
    string ResultsDirectory,
    string BinlogPath,
    string TempDirectory)
{
    private const int GenerationHashLength = 12;
    private const int MaxOrdinal = 999_999;
    private const int MaxAllocationAttempts = 64;
    private const int ReapSuffixHexLength = 8;

    internal const string AllocationMarkerFileName = ".allocated";

    internal const string ReapSuffixPrefix = ".reap-";

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(OutDir);
        Directory.CreateDirectory(ResultsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(BinlogPath)!);
        Directory.CreateDirectory(TempDirectory);
    }

    public static CtGenerationPaths Allocate(ContinuousTestWorkspace workspace)
        => Allocate(workspace, beforeMarkerCreated: null);

    internal static CtGenerationPaths Allocate(
        ContinuousTestWorkspace workspace,
        Action<int>? beforeMarkerCreated)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        Directory.CreateDirectory(workspace.BuildOutputRoot);
        var ordinal = HighestOrdinal(workspace.BuildOutputRoot) + 1;

        for (var attempt = 0; attempt < MaxAllocationAttempts; attempt++, ordinal++)
        {
            if (ordinal > MaxOrdinal)
                throw new InvalidOperationException(
                    $"continuous-test generation ordinals are exhausted under {workspace.BuildOutputRoot}");

            var generationId = IdForOrdinal(workspace, ordinal);
            var generationRoot = Path.Combine(workspace.BuildOutputRoot, generationId);
            Directory.CreateDirectory(generationRoot);

            beforeMarkerCreated?.Invoke(ordinal);

            if (!TryClaimMarker(generationRoot, ordinal))
                continue;

            return For(workspace, generationId);
        }

        throw new IOException(
            $"could not claim a continuous-test generation under {workspace.BuildOutputRoot} after {MaxAllocationAttempts} attempts");
    }

    public static CtGenerationPaths ResolveLatestOrFirst(ContinuousTestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var highest = HighestOrdinal(workspace.BuildOutputRoot);
        return For(workspace, IdForOrdinal(workspace, highest == 0 ? 1 : highest));
    }

    internal static CtGenerationPaths For(ContinuousTestWorkspace workspace, string generationId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrEmpty(generationId);

        var generationRoot = Path.Combine(workspace.BuildOutputRoot, generationId);
        return new CtGenerationPaths(
            GenerationId: generationId,
            GenerationRoot: generationRoot,
            OutDir: WithTrailingSeparator(Path.Combine(generationRoot, "out")),
            ResultsDirectory: WithTrailingSeparator(Path.Combine(generationRoot, "TestResults")),
            BinlogPath: Path.Combine(generationRoot, "logs", "build.binlog"),
            TempDirectory: CtTempPaths.ForGeneration(workspace, generationId));
    }

    public static bool TryReap(string generationRoot)
        => TryReap(
            generationRoot,
            static (source, destination) => Directory.Move(source, destination),
            static directory => Directory.Delete(directory, recursive: true));

    internal static bool TryReap(
        string generationRoot,
        Action<string, string> renameDirectory,
        Action<string> deleteDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(generationRoot);
        ArgumentNullException.ThrowIfNull(renameDirectory);
        ArgumentNullException.ThrowIfNull(deleteDirectory);

        if (!Directory.Exists(generationRoot))
            return true;

        var reapRoot = generationRoot + ReapSuffixPrefix + RandomHexSuffix();
        try
        {
            renameDirectory(generationRoot, reapRoot);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            deleteDirectory(reapRoot);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return true;
    }

    public static bool IsGenerationId(string? directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName)
            || directoryName.Length != 1 + GenerationHashLength
            || directoryName[0] != 'g')
            return false;

        for (var i = 1; i < directoryName.Length; i++)
        {
            var c = directoryName[i];
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    internal static string IdForOrdinal(ContinuousTestWorkspace workspace, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);

        var seed = workspace.BuildOutputRoot + "\0" + ordinal.ToString(CultureInfo.InvariantCulture);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))
            .ToLowerInvariant()[..GenerationHashLength];
        return "g" + hash;
    }

    private static bool TryClaimMarker(string generationRoot, int ordinal)
    {
        try
        {
            using var marker = new FileStream(
                Path.Combine(generationRoot, AllocationMarkerFileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            var payload = Encoding.ASCII.GetBytes(ordinal.ToString(CultureInfo.InvariantCulture));
            marker.Write(payload);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int HighestOrdinal(string buildOutputRoot)
    {
        if (!Directory.Exists(buildOutputRoot))
            return 0;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(buildOutputRoot).ToArray();
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        var highest = 0;
        foreach (var directory in directories)
        {
            if (!IsGenerationId(Path.GetFileName(directory)))
                continue;
            if (TryReadOrdinal(directory, out var ordinal) && ordinal > highest)
                highest = ordinal;
        }

        return highest;
    }

    private static bool TryReadOrdinal(string generationRoot, out int ordinal)
    {
        ordinal = 0;
        try
        {
            var text = File.ReadAllText(Path.Combine(generationRoot, AllocationMarkerFileName)).Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
                   && ordinal > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string RandomHexSuffix()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(ReapSuffixHexLength / 2)).ToLowerInvariant();

    private static string WithTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
