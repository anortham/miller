using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Miller.Indexing;

/// <summary>
/// Subprocess wrapper over the pinned <c>julie-server extract</c> binary. Builds argv via
/// <see cref="ProcessStartInfo.ArgumentList"/> (no shell), captures stdout/stderr separately, and maps the
/// exit code to a typed outcome: 0 → parsed <see cref="ExtractReport"/>; 1 → <see cref="JulieExtractFailedException"/>
/// (the failed report's errors + stderr); 2 → <see cref="JulieExtractUsageException"/> (stderr-only); else →
/// <see cref="JulieExtractException"/>. All paths passed to julie are absolute. On an exit-0 success the
/// runner additionally cross-checks the report's schema/contract versions against
/// <see cref="MillerExtractContract"/> and throws <see cref="IncompatibleExtractException"/> on a mismatch
/// (julie only self-rejects a *newer* DB, so catching an older/drifted one is Miller's job — D5).
///
/// The pure seams (argv builders, <see cref="ParseReport"/>, <see cref="Interpret"/>) are static so the
/// contract suite pins them without spawning a process; the live spawn is the Scale suite.
/// </summary>
public sealed class JulieExtractRunner
{
    // info opens read-only, takes no flock — `extract --db <ABS_DB> --json info` (NO --root).
    // scan binds the workspace/root — `extract --db <ABS_DB> --root <ABS_ROOT> --json scan [--force]`.
    // update/delete touch one canonical file — `... --json update|delete --file <ABS_CANON_FILE>` (M3).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _binaryPath;

    /// <summary>The resolved absolute path to the julie-server binary this runner invokes.</summary>
    public string BinaryPath => _binaryPath;

    /// <summary>
    /// Create a runner bound to a specific binary path. Throws if it does not exist, pointing the operator at
    /// the restore script. Use <see cref="Locate"/> for the default resolution.
    /// </summary>
    /// <exception cref="FileNotFoundException">The binary does not exist at <paramref name="binaryPath"/>.</exception>
    public JulieExtractRunner(string binaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        string abs = Path.GetFullPath(binaryPath);
        if (!File.Exists(abs))
            throw new FileNotFoundException(
                $"julie-server binary not found at '{abs}'. Run scripts/restore-julie-server.sh " +
                "(or restore-julie-server.ps1 on Windows) to download the pinned " +
                $"v{MillerExtractContract.PinnedJulieServerVersion} binary into .tools/.", abs);
        _binaryPath = abs;
    }

    /// <summary>
    /// Resolve the julie-server binary: <c>.tools/julie-server[.exe]</c> under <paramref name="toolsRoot"/>
    /// first, then PATH. Returns a constructed runner, or throws <see cref="FileNotFoundException"/> pointing at
    /// the restore script if absent.
    /// </summary>
    public static JulieExtractRunner Locate(string toolsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        string binaryName = OperatingSystem.IsWindows() ? "julie-server.exe" : "julie-server";
        string toolsCandidate = Path.Combine(toolsRoot, binaryName);
        if (File.Exists(toolsCandidate))
            return new JulieExtractRunner(toolsCandidate);

        string? onPath = FindOnPath(binaryName);
        if (onPath is not null)
            return new JulieExtractRunner(onPath);

        throw new FileNotFoundException(
            $"julie-server not found in '{toolsRoot}' or on PATH. Run scripts/restore-julie-server.sh " +
            "(or restore-julie-server.ps1 on Windows) to download the pinned " +
            $"v{MillerExtractContract.PinnedJulieServerVersion} binary into .tools/.", toolsCandidate);
    }

    private static string? FindOnPath(string binaryName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, binaryName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // ---------- pure seams (testable without a process) ----------

    /// <summary>
    /// Build the argv for a <c>scan</c>: <c>extract --db &lt;absDb&gt; --root &lt;absRoot&gt; --json scan [--force]</c>.
    /// Paths must already be absolute (caller's responsibility for relative-CWD safety).
    /// </summary>
    public static IReadOnlyList<string> BuildScanArgs(string absDb, string absRoot, bool force)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absDb);
        ArgumentException.ThrowIfNullOrWhiteSpace(absRoot);
        var args = new List<string> { "extract", "--db", absDb, "--root", absRoot, "--json", "scan" };
        if (force)
            args.Add("--force");
        return args;
    }

    /// <summary>
    /// Build the argv for <c>info</c>: <c>extract --db &lt;absDb&gt; --json info</c>. No <c>--root</c> (info
    /// opens read-only, takes no flock — safe under a live writer).
    /// </summary>
    public static IReadOnlyList<string> BuildInfoArgs(string absDb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absDb);
        return new[] { "extract", "--db", absDb, "--json", "info" };
    }

    /// <summary>
    /// Build the argv for a single-file <c>update</c>:
    /// <c>extract --db &lt;absDb&gt; --root &lt;absRoot&gt; --json update --file &lt;absFile&gt;</c>. All three
    /// paths must be CANONICAL (absolute + symlink-resolved — see <see cref="PathCanonicalizer"/>) so julie's
    /// inside-root check passes (verified-fact 4). The builder is a pure seam: it does NOT re-normalize, so the
    /// caller's canonical paths reach julie verbatim.
    /// </summary>
    public static IReadOnlyList<string> BuildUpdateArgs(string absDb, string absRoot, string absFile) =>
        BuildFileOpArgs("update", absDb, absRoot, absFile);

    /// <summary>
    /// Build the argv for a single-file <c>delete</c>:
    /// <c>extract --db &lt;absDb&gt; --root &lt;absRoot&gt; --json delete --file &lt;absFile&gt;</c>. Same
    /// canonical-path contract as <see cref="BuildUpdateArgs"/> — this is the exact call the path gotcha
    /// (verified-fact 4) governs: a non-canonical <c>--file</c> under a symlinked root is rejected.
    /// </summary>
    public static IReadOnlyList<string> BuildDeleteArgs(string absDb, string absRoot, string absFile) =>
        BuildFileOpArgs("delete", absDb, absRoot, absFile);

    // Shared shape for the two single-file ops (the only difference is the subcommand token).
    private static IReadOnlyList<string> BuildFileOpArgs(
        string subcommand, string absDb, string absRoot, string absFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absDb);
        ArgumentException.ThrowIfNullOrWhiteSpace(absRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(absFile);
        return new[] { "extract", "--db", absDb, "--root", absRoot, "--json", subcommand, "--file", absFile };
    }

    /// <summary>Parse a julie extract report from stdout JSON.</summary>
    /// <exception cref="JsonException">The text is not a valid <see cref="ExtractReport"/>.</exception>
    public static ExtractReport ParseReport(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<ExtractReport>(json, JsonOpts)
            ?? throw new JsonException("julie extract report deserialized to null.");
    }

    /// <summary>
    /// Map a completed process result (exit code + captured stdout/stderr) to a typed outcome. This is the
    /// exit-code contract: 0 → parsed report; 1 → <see cref="JulieExtractFailedException"/>; 2 →
    /// <see cref="JulieExtractUsageException"/>; else → <see cref="JulieExtractException"/>.
    /// </summary>
    public static ExtractReport Interpret(int exitCode, string stdout, string stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        switch (exitCode)
        {
            case 0:
                return ParseReport(stdout);

            case 1:
                // stdout STILL holds a failed report; recover its errors. If stdout is unparseable, still
                // surface a failure (never a silent success) carrying stderr.
                IReadOnlyList<ExtractError> errors;
                try
                {
                    errors = ParseReport(stdout).Errors;
                }
                catch (JsonException)
                {
                    errors = Array.Empty<ExtractError>();
                }

                string codes = errors.Count == 0
                    ? "(no structured errors)"
                    : string.Join(", ", errors.Select(e => e.Code));
                throw new JulieExtractFailedException(
                    $"julie-server extract failed (exit 1): {codes}.", errors, stderr);

            case 2:
                // Usage/argv error: NO JSON on stdout, clap usage text on stderr. Do not parse stdout.
                throw new JulieExtractUsageException(stderr);

            default:
                throw new JulieExtractException(
                    $"julie-server extract exited with unexpected code {exitCode}.", stderr);
        }
    }

    // ---------- live invocation (Scale path / M3) ----------

    /// <summary>
    /// Run <c>extract scan</c>. Ensures the DB's parent directory exists (julie does not mkdir), resolves both
    /// paths to absolute, invokes the binary, and interprets the result. The first call on a fresh DB MUST be
    /// a scan (binds workspace_id + root); pass <paramref name="force"/> for a full rebuild / root change.
    /// </summary>
    public ExtractReport Scan(string root, string db, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(db);
        string absDb = Path.GetFullPath(db);
        string absRoot = Path.GetFullPath(root);

        string? dbDir = Path.GetDirectoryName(absDb);
        if (!string.IsNullOrEmpty(dbDir))
            Directory.CreateDirectory(dbDir); // no mkdir in julie's path; the .db itself may be absent (fresh)

        return Run(BuildScanArgs(absDb, absRoot, force));
    }

    /// <summary>Run <c>extract info</c> (read-only, no flock) and return the parsed report.</summary>
    public ExtractReport Info(string db)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(db);
        return Run(BuildInfoArgs(Path.GetFullPath(db)));
    }

    /// <summary>
    /// Run <c>extract update --file</c> on a single CHANGED file. julie blake3-checks the content and no-ops
    /// (<c>status=unchanged</c>, no revision bump) if it is identical, so this is safe to call on any event.
    /// ALL THREE paths (<paramref name="canonicalDb"/>, <paramref name="canonicalRoot"/>,
    /// <paramref name="canonicalFile"/>) MUST already be canonical (absolute + symlink-resolved via
    /// <see cref="PathCanonicalizer"/>) — this method does NOT re-canonicalize (and deliberately does not even
    /// lexically <see cref="Path.GetFullPath(string)"/> the db, which would not resolve symlinks anyway), to
    /// preserve the verified-fact-4 fix: a non-canonical <c>--db</c>/<c>--root</c>/<c>--file</c> under a
    /// symlinked workspace trips julie's outside-root validation. The bootstrap canonicalizes the db ONCE and
    /// passes it here verbatim. Routes through the same <see cref="Run"/> → <see cref="Interpret"/> → version
    /// cross-check as <see cref="Scan"/>; the exit-code contract is identical.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="canonicalDb"/> is null/blank or not an absolute path.</exception>
    public ExtractReport Update(string canonicalRoot, string canonicalDb, string canonicalFile)
    {
        RequireCanonicalDb(canonicalDb);
        return Run(BuildUpdateArgs(canonicalDb, canonicalRoot, canonicalFile));
    }

    /// <summary>
    /// Run <c>extract delete --file</c> on a single REMOVED file. Idempotent: a second delete reports
    /// <c>status=not_found</c> (exit 0), not a failure. Same canonical-path contract and exit-code routing as
    /// <see cref="Update"/>. This is the exact call verified-fact-4 governs — the db, root, AND file MUST all be
    /// symlink-resolved, passed through verbatim (no <see cref="Path.GetFullPath(string)"/> re-mangling).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="canonicalDb"/> is null/blank or not an absolute path.</exception>
    public ExtractReport Delete(string canonicalRoot, string canonicalDb, string canonicalFile)
    {
        RequireCanonicalDb(canonicalDb);
        return Run(BuildDeleteArgs(canonicalDb, canonicalRoot, canonicalFile));
    }

    // The single-file ops (verified-fact 4) take an ALREADY-canonical db. We guard that it is at least an
    // absolute path here — a relative db would be resolved against the ambient CWD by julie, defeating the
    // canonicalization the bootstrap performed. (BuildUpdateArgs/BuildDeleteArgs reject null/blank.)
    private static void RequireCanonicalDb(string canonicalDb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDb);
        if (!Path.IsPathRooted(canonicalDb))
            throw new ArgumentException(
                $"The extract db path '{canonicalDb}' must be an absolute, canonical path (the bootstrap " +
                "canonicalizes it once under the symlink-resolved root — verified-fact 4).", nameof(canonicalDb));
    }

    private ExtractReport Run(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
                throw new JulieExtractException(
                    $"Failed to start julie-server at '{_binaryPath}'.", standardError: string.Empty);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // UseShellExecute=false: a failed exec (wrong-arch binary → "Exec format error",
            // non-executable, permission denied) throws Win32Exception rather than returning false.
            // Wrap it in the typed contract so a botched restore surfaces as a JulieExtractException.
            throw new JulieExtractException(
                $"Failed to exec julie-server at '{_binaryPath}' (corrupt/wrong-arch/non-executable binary? " +
                $"re-run scripts/restore-julie-server.sh). {ex.Message}",
                standardError: string.Empty, ex);
        }

        // Read stdout/stderr asynchronously to avoid the classic pipe-buffer deadlock on large reports.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        ExtractReport report = Interpret(
            process.ExitCode, stdout.ToString().TrimEnd('\n', '\r'), stderr.ToString().TrimEnd('\n', '\r'));

        // Post-extract cross-check: julie only self-rejects a *newer* DB, so an older/drifted schema or
        // contract that julie tolerated is Miller's gate to enforce (D5). Surface a typed mismatch at the
        // extract boundary with the same wording the read-path gate uses.
        ExtractVersionMismatch.VerifyReport(report);
        return report;
    }
}
