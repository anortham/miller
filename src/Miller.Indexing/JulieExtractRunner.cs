using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Miller.Indexing;

/// <summary>
/// Subprocess wrapper over the pinned <c>julie-extract</c> binary. Builds argv via
/// <see cref="ProcessStartInfo.ArgumentList"/> (no shell), captures stdout/stderr separately, and maps the
/// exit code to a typed outcome: 0 → parsed <see cref="ExtractReport"/>; 1 → either a returned partial report
/// (consistent artifact) or <see cref="JulieExtractFailedException"/> (the failed report's diagnostics + stderr);
/// 2 → <see cref="JulieExtractUsageException"/> (stderr-only); 3 → <see cref="IncompatibleExtractException"/>
/// (schema/contract/root incompatible); else → <see cref="JulieExtractException"/>. All paths passed to
/// julie-extract are absolute. On an exit-0 success the runner additionally cross-checks the report's
/// artifact schema/contract versions against <see cref="MillerExtractContract"/> and throws
/// <see cref="IncompatibleExtractException"/> on a mismatch (julie-extract only self-rejects a *newer* DB, so
/// catching an older/drifted one is Miller's job — D5).
///
/// The pure seams (argv builders, <see cref="ParseReport"/>, <see cref="Interpret"/>) are static so the
/// contract suite pins them without spawning a process; the live spawn is the Scale suite. The live
/// <see cref="Run"/> bounds the wait on a hung child (<see cref="DefaultTimeout"/>) and kills the process tree
/// on timeout so a wedged bootstrap can never hang the host graph (§10A).
/// </summary>
public sealed class JulieExtractRunner
{
    // info opens read-only, takes no flock — `info --db <ABS_DB> --strict-schema --json` (NO --root).
    // scan binds the workspace/root — `scan --root <ABS_ROOT> --db <ABS_DB> --strict-schema --json [--force]`.
    // update/delete touch one canonical file — `update|delete --root <ABS_ROOT> --db <ABS_DB> --file <ABS_CANON_FILE> --strict-schema --json` (M3).
    /// <summary>
    /// The default bound on a single julie-extract invocation. Generous (a cold full scan of a large repo is
    /// well under this); the purpose is to bound a truly hung child, not to clip a legitimate slow scan (§10A).
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly string _binaryPath;
    private readonly TimeSpan _timeout;

    /// <summary>The resolved absolute path to the julie-extract binary this runner invokes.</summary>
    public string BinaryPath => _binaryPath;

    /// <summary>
    /// Create a runner bound to a specific binary path with the <see cref="DefaultTimeout"/>. Throws if the
    /// binary does not exist, pointing the operator at the restore script. Use <see cref="Locate"/> for the
    /// default resolution.
    /// </summary>
    /// <exception cref="FileNotFoundException">The binary does not exist at <paramref name="binaryPath"/>.</exception>
    public JulieExtractRunner(string binaryPath) : this(binaryPath, DefaultTimeout)
    {
    }

    /// <summary>
    /// Create a runner bound to a specific binary path and a per-invocation timeout (test seam — the Scale
    /// timeout test passes a tiny value to force the hung-process kill path).
    /// </summary>
    /// <exception cref="FileNotFoundException">The binary does not exist at <paramref name="binaryPath"/>.</exception>
    public JulieExtractRunner(string binaryPath, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        string abs = Path.GetFullPath(binaryPath);
        if (!File.Exists(abs))
            throw new FileNotFoundException(
                $"julie-extract binary not found at '{abs}'. Run scripts/restore-julie-extract.sh " +
                "(or restore-julie-extract.ps1 on Windows) to download the pinned " +
                $"v{MillerExtractContract.PinnedJulieExtractVersion} binary into .tools/.", abs);
        _binaryPath = abs;
        _timeout = timeout;
    }

    /// <summary>
    /// Resolve the julie-extract binary: <c>.tools/julie-extract[.exe]</c> under <paramref name="toolsRoot"/>
    /// first, then PATH. Returns a constructed runner, or throws <see cref="FileNotFoundException"/> pointing at
    /// the restore script if absent.
    /// </summary>
    public static JulieExtractRunner Locate(string toolsRoot) =>
        Locate(toolsRoot, (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Resolve the julie-extract binary using <paramref name="pathDirs"/> as the PATH search list (test seam —
    /// avoids ambient PATH flakiness when <c>julie-extract</c> is installed on a dev or CI machine).
    /// </summary>
    internal static JulieExtractRunner Locate(string toolsRoot, IReadOnlyList<string> pathDirs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsRoot);
        string binaryName = OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract";
        string toolsCandidate = Path.Combine(toolsRoot, binaryName);
        if (File.Exists(toolsCandidate))
            return new JulieExtractRunner(toolsCandidate);

        string? onPath = FindOnPath(binaryName, pathDirs);
        if (onPath is not null)
            return new JulieExtractRunner(onPath);

        throw new FileNotFoundException(
            $"julie-extract not found in '{toolsRoot}' or on PATH. Run scripts/restore-julie-extract.sh " +
            "(or restore-julie-extract.ps1 on Windows) to download the pinned " +
            $"v{MillerExtractContract.PinnedJulieExtractVersion} binary into .tools/.", toolsCandidate);
    }

    private static string? FindOnPath(string binaryName, IReadOnlyList<string> pathDirs)
    {
        foreach (var dir in pathDirs)
        {
            string candidate = Path.Combine(dir, binaryName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // ---------- pure seams (testable without a process) ----------

    /// <summary>
    /// Build the argv for a <c>scan</c>:
    /// <c>scan --root &lt;absRoot&gt; --db &lt;absDb&gt; --strict-schema --json [--force]</c>.
    /// v1 is a top-level subcommand with no parent verb and no workspace-id flag (the artifact binds the root
    /// itself). Paths must already be absolute (caller's responsibility for relative-CWD safety).
    /// </summary>
    public static IReadOnlyList<string> BuildScanArgs(string absDb, string absRoot, bool force)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absDb);
        ArgumentException.ThrowIfNullOrWhiteSpace(absRoot);
        var args = new List<string>
            { "scan", "--root", absRoot, "--db", absDb, "--strict-schema", "--json" };
        if (force)
            args.Add("--force");
        return args;
    }

    /// <summary>
    /// Build the argv for <c>info</c>: <c>info --db &lt;absDb&gt; --strict-schema --json</c>. No <c>--root</c>
    /// (info opens read-only, takes no flock — safe under a live writer).
    /// </summary>
    public static IReadOnlyList<string> BuildInfoArgs(string absDb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absDb);
        return new[] { "info", "--db", absDb, "--strict-schema", "--json" };
    }

    /// <summary>
    /// Build the argv for a single-file <c>update</c>:
    /// <c>update --root &lt;absRoot&gt; --db &lt;absDb&gt; --file &lt;absFile&gt; --strict-schema --json</c>. All
    /// three paths must be CANONICAL (absolute + symlink-resolved — see <see cref="PathCanonicalizer"/>) so
    /// julie-extract's inside-root check passes (verified-fact 4). The builder is a pure seam: it does NOT
    /// re-normalize, so the caller's canonical paths reach julie verbatim.
    /// </summary>
    public static IReadOnlyList<string> BuildUpdateArgs(string absDb, string absRoot, string absFile) =>
        BuildFileOpArgs("update", absDb, absRoot, absFile);

    /// <summary>
    /// Build the argv for a single-file <c>delete</c>:
    /// <c>delete --root &lt;absRoot&gt; --db &lt;absDb&gt; --file &lt;absFile&gt; --strict-schema --json</c>. Same
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
        return new[] { subcommand, "--root", absRoot, "--db", absDb, "--file", absFile, "--strict-schema", "--json" };
    }

    /// <summary>Parse a julie-extract report from stdout JSON.</summary>
    /// <exception cref="JsonException">The text is not a valid <see cref="ExtractReport"/>.</exception>
    public static ExtractReport ParseReport(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, JulieExtractJsonContext.Default.ExtractReport)
            ?? throw new JsonException("julie-extract report deserialized to null.");
    }

    /// <summary>
    /// Map a completed process result (exit code + captured stdout/stderr) to a typed outcome. v1's four-code
    /// contract: 0 → parsed report; 1 → a returned partial report (consistent artifact) OR
    /// <see cref="JulieExtractFailedException"/> (status=="failed"/unparseable); 2 →
    /// <see cref="JulieExtractUsageException"/> (stderr-only); 3 → <see cref="IncompatibleExtractException"/>
    /// (schema/contract/root incompatible); else → base <see cref="JulieExtractException"/>.
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
                // stdout STILL holds a report. Two sub-cases (reconciliation #10):
                //  - status=="partial": some files failed to parse but the artifact is CONSISTENT
                //    (.with_artifact + rows_written + revision; commands.rs:217-251). RETURN it so bootstrap
                //    loads the usable rows; the caller logs counts.files_failed + errors[] as a WARNING.
                //    Aborting the whole index build because one file failed is wrong (README "Reports And Exit Status").
                //  - status=="failed" (or unparseable): a real failure → throw with the structured diagnostics.
                //    Path errors (file_outside_root/invalid_path/file_not_found) are status=="failed" here, NOT exit 3.
                ExtractReport? report1 = null;
                IReadOnlyList<ReportDiagnostic> errors;
                try { report1 = ParseReport(stdout); errors = report1.Errors; }
                catch (JsonException) { errors = Array.Empty<ReportDiagnostic>(); }

                if (report1 is { Status: "partial" })
                    return report1; // consistent artifact; caller WARN-logs files_failed + errors[]

                string codes = errors.Count == 0
                    ? "(no structured errors)"
                    : string.Join(", ", errors.Select(e => e.Code));
                throw new JulieExtractFailedException(
                    $"julie-extract failed (exit 1): {codes}.", errors, stderr);

            case 2:
                // Usage/argv error: NO JSON on stdout, clap usage text on stderr. Do not parse stdout.
                throw new JulieExtractUsageException(stderr);

            case 3:
                // Incompatible schema/contract/root (schema_incompatible / schema_migration_required /
                // contract_incompatible / root_mismatch). stdout holds a failed report; surface its code as the
                // SAME typed signal the read-path gate throws. Defensive: an unparseable stdout still throws
                // incompatible (carrying stderr), never a silent pass.
                string code;
                try
                {
                    var report = ParseReport(stdout);
                    code = report.Errors.Count > 0 ? report.Errors[0].Code : "(no structured errors)";
                }
                catch (JsonException)
                {
                    code = string.IsNullOrWhiteSpace(stderr) ? "(unparseable report)" : stderr;
                }
                throw new IncompatibleExtractException(
                    $"julie-extract reported an incompatible artifact (exit 3): {code}. " +
                    "Re-run restore + `julie-extract scan` with the pinned julie-extract " +
                    $"(v{MillerExtractContract.PinnedJulieExtractVersion}).");

            default:
                throw new JulieExtractException(
                    $"julie-extract exited with unexpected code {exitCode}.", stderr);
        }
    }

    // ---------- live invocation (Scale path / M3) ----------

    /// <summary>
    /// Run <c>julie-extract scan</c>. Ensures the DB's parent directory exists (julie-extract does not mkdir),
    /// resolves both paths to absolute, invokes the binary, and interprets the result. The first call on a fresh
    /// DB MUST be a scan (binds the root); pass <paramref name="force"/> for a full rebuild / root change.
    /// </summary>
    public ExtractReport Scan(string root, string db, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(db);
        string absDb = Path.GetFullPath(db);
        string absRoot = Path.GetFullPath(root);

        string? dbDir = Path.GetDirectoryName(absDb);
        if (!string.IsNullOrEmpty(dbDir))
            Directory.CreateDirectory(dbDir); // no mkdir in julie-extract's path; the .db itself may be absent (fresh)

        return Run(BuildScanArgs(absDb, absRoot, force));
    }

    /// <summary>Run <c>julie-extract info</c> (read-only, no flock) and return the parsed report.</summary>
    public ExtractReport Info(string db)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(db);
        return Run(BuildInfoArgs(Path.GetFullPath(db)));
    }

    /// <summary>
    /// Run <c>julie-extract update --file</c> on a single CHANGED file. julie-extract blake3-checks the content
    /// and no-ops (<c>status=no_change</c>, no revision bump) if it is identical, so this is safe to call on any event.
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
    /// Run <c>julie-extract delete --file</c> on a single REMOVED file. Idempotent: a second delete reports
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
                    $"Failed to start julie-extract at '{_binaryPath}'.", standardError: string.Empty);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // UseShellExecute=false: a failed exec (wrong-arch binary → "Exec format error",
            // non-executable, permission denied) throws Win32Exception rather than returning false.
            // Wrap it in the typed contract so a botched restore surfaces as a JulieExtractException.
            throw new JulieExtractException(
                $"Failed to exec julie-extract at '{_binaryPath}' (corrupt/wrong-arch/non-executable binary? " +
                $"re-run scripts/restore-julie-extract.sh). {ex.Message}",
                standardError: string.Empty, ex);
        }

        // Read stdout/stderr asynchronously to avoid the classic pipe-buffer deadlock on large reports.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Bound the wait: a hung julie-extract would otherwise block a hosted-service StartAsync forever and
        // wedge the whole host graph (CLAUDE.md host-lifecycle gotcha; §10A). On timeout, kill the process
        // tree and surface a typed, actionable failure — never a silent hang.
        if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited between the wait and the kill */ }
            process.WaitForExit(); // reap the killed child so the handle is released
            throw new JulieExtractException(
                $"julie-extract at '{_binaryPath}' timed out after {_timeout.TotalSeconds:0}s and was killed " +
                "(possible hang / wrong binary). Re-run scripts/restore-julie-extract.sh if this persists.",
                standardError: stderr.ToString().TrimEnd('\n', '\r'));
        }

        // The timed WaitForExit(int) overload returns as soon as the process exits but does NOT wait for the
        // async output handlers (BeginOutputReadLine/BeginErrorReadLine) to flush their final buffers — so the
        // StringBuilders can still be empty/partial here. The parameterless WaitForExit() blocks until the
        // redirected streams are fully drained (documented .NET requirement when mixing the timed overload with
        // async reads). Without it, ParseReport sees an empty stdout and throws "no JSON tokens" even though the
        // child wrote a complete report. The process has already exited, so this returns promptly.
        process.WaitForExit();

        ExtractReport report = Interpret(
            process.ExitCode, stdout.ToString().TrimEnd('\n', '\r'), stderr.ToString().TrimEnd('\n', '\r'));

        // Post-extract cross-check: julie-extract only self-rejects a *newer* DB, so an older/drifted schema or
        // contract that it tolerated is Miller's gate to enforce (D5). Surface a typed mismatch at the
        // extract boundary with the same wording the read-path gate uses.
        ExtractVersionMismatch.VerifyReport(report);
        return report;
    }
}
