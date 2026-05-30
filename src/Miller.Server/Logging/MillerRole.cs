using Serilog.Core;
using Serilog.Events;

namespace Miller.Server.Logging;

/// <summary>
/// The process-wide leader/reader role rendered onto every log line as the <c>role</c> property (m8-design §D2/§D1).
/// Leadership is won LATER (in <c>IndexerService</c>, after the writer-lock race), so the role cannot be a startup
/// <c>Enrich.WithProperty</c> constant the way <c>pid</c> is — it must be a MUTABLE enricher whose value the
/// indexer flips on the lease-won / step-down transitions. Until leadership is resolved the role is
/// <see cref="Reader"/> (the honest default: an instance that has not won the lock is a reader).
///
/// <para><b>Why a property, not the file path.</b> §D1 keeps role OUT of the per-pid file name precisely because
/// it is not known at file-open time; carrying it as a log property lets a leader's and a reader's lines be told
/// apart in the SHARED <c>&lt;root&gt;/.miller/logs</c> directory (the M3 multi-process debuggability goal) without
/// renaming the open sink. Both the human <c>.log</c> and the machine <c>.jsonl</c> render it.</para>
///
/// <para>The current value is a single <see cref="Volatile"/> field: writes (the rare leader/reader transition on
/// the indexer thread) publish immediately to the enricher reads (every emitted event, on any thread).</para>
/// </summary>
public static class MillerRole
{
    /// <summary>The role value for an instance that holds the writer lock and runs the watcher.</summary>
    public const string Leader = "leader";

    /// <summary>The role value for a non-leader instance (the honest default until leadership is resolved).</summary>
    public const string Reader = "reader";

    // The live role. Starts as Reader: every instance is a reader until it wins the lock. Read by the enricher on
    // every event (any thread); written only on the leader-won / step-down transition (the indexer thread).
    private static volatile string _current = Reader;

    /// <summary>The current process role rendered onto log lines (<see cref="Leader"/> or <see cref="Reader"/>).</summary>
    public static string Current => _current;

    /// <summary>Flip the live role to <see cref="Leader"/> — called by <c>IndexerService</c> once the lease is won.</summary>
    public static void SetLeader() => _current = Leader;

    /// <summary>Flip the live role to <see cref="Reader"/> — the startup default and the leader's step-down transition.</summary>
    public static void SetReader() => _current = Reader;

    /// <summary>Set the role explicitly (used to restore a saved role in tests). Null/empty resets to <see cref="Reader"/>.</summary>
    public static void Set(string? role) => _current = string.IsNullOrEmpty(role) ? Reader : role;

    /// <summary>
    /// The Serilog enricher that stamps the LIVE <see cref="Current"/> role onto every event as the <c>role</c>
    /// property. Re-reads the field per event (not a captured constant) so a leader/reader transition is reflected
    /// on the very next line.
    /// </summary>
    public static ILogEventEnricher Enricher { get; } = new RoleEnricher();

    private sealed class RoleEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            ArgumentNullException.ThrowIfNull(propertyFactory);
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("role", _current));
        }
    }
}
