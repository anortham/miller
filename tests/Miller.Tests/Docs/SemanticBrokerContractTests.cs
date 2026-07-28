using Xunit;

namespace Miller.Tests.Docs;

public sealed class SemanticBrokerContractTests
{
    private static readonly string ContractPath = Path.Combine(
        ScaleTestSupport.RepoRoot(),
        "docs",
        "contracts",
        "semantic-broker-v1.md");

    [Fact]
    public void BrokerContract_LocksTheFailureProneLifecycleOut()
    {
        string text = File.ReadAllText(ContractPath);

        Assert.Contains("stdin EOF", text, StringComparison.Ordinal);
        Assert.Contains("JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE", text, StringComparison.Ordinal);
        Assert.Contains("No PID file", text, StringComparison.Ordinal);
        Assert.Contains("No broker-initiated restart", text, StringComparison.Ordinal);
        Assert.Contains("PIPE_REJECT_REMOTE_CLIENTS", text, StringComparison.Ordinal);
        Assert.Contains(
            "julie.semantic.broker|1|julie.embedding.sidecar|1|",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "shutdown closes only the requesting connection",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerContract_FreezesIdentityTransportAndScheduling()
    {
        string text = File.ReadAllText(ContractPath);

        Assert.Contains("julie-semantic-sidecar broker", text, StringComparison.Ordinal);
        Assert.Contains("--model <model-id>", text, StringComparison.Ordinal);
        Assert.Contains("--endpoint <uds-path-or-full-pipe-name>", text, StringComparison.Ordinal);
        Assert.Contains("--lock <model-service-lock-path>", text, StringComparison.Ordinal);
        Assert.Contains(
            "--accelerator-lock <user-global-accelerator-lock-path>",
            text,
            StringComparison.Ordinal);
        Assert.Contains("lowercase_hex(sha256(UTF8(identity_input)))[0..16]", text, StringComparison.Ordinal);
        Assert.Contains("one request in flight per connection", text, StringComparison.Ordinal);
        Assert.Contains("multiple connections per broker", text, StringComparison.Ordinal);
        Assert.Contains("full 120-second initialization budget", text, StringComparison.Ordinal);
        Assert.Contains("capacity is 64", text, StringComparison.Ordinal);
        Assert.Contains("at most eight interactive dequeues", text, StringComparison.Ordinal);
        Assert.Contains("60-second active-request watchdog", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerContract_FreezesSecurityOwnershipAndFailOpenBehavior()
    {
        string text = File.ReadAllText(ContractPath);

        Assert.Contains("0700", text, StringComparison.Ordinal);
        Assert.Contains("0600", text, StringComparison.Ordinal);
        Assert.Contains(@"\\.\pipe\<name>", text, StringComparison.Ordinal);
        Assert.Contains("NamedPipeClientStream", text, StringComparison.Ordinal);
        Assert.Contains("current-user ACL", text, StringComparison.Ordinal);
        Assert.Contains("cancellable I/O", text, StringComparison.Ordinal);
        Assert.Contains("service-lock holder", text, StringComparison.Ordinal);
        Assert.Contains("Owner disposal", text, StringComparison.Ordinal);
        Assert.Contains("Non-owner disposal", text, StringComparison.Ordinal);
        Assert.Contains("ResourceExhausted", text, StringComparison.Ordinal);
        Assert.Contains("ContextAlloc", text, StringComparison.Ordinal);
        Assert.Contains("retries the failed request once", text, StringComparison.Ordinal);
        Assert.Contains("Decode", text, StringComparison.Ordinal);
        Assert.Contains("Encode", text, StringComparison.Ordinal);
        Assert.Contains("MILLER_SEMANTIC=off", text, StringComparison.Ordinal);
        Assert.Contains("zero work", text, StringComparison.Ordinal);
        Assert.Contains("No new MCP tool", text, StringComparison.Ordinal);
        Assert.Contains("approval-gated", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerContract_SeparatesOwnerLeaseFromServiceAndAcceleratorLocks()
    {
        string text = File.ReadAllText(ContractPath);

        Assert.Contains("The spawning Miller factory is the owner", text, StringComparison.Ordinal);
        Assert.Contains("The sidecar process is the service broker", text, StringComparison.Ordinal);
        Assert.Contains("service broker holds the model service lock", text, StringComparison.Ordinal);
        Assert.Contains("spawning Miller factory retains the owner stdin lease", text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "contender that acquires the service lock is the owner",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerContract_ReusesTheFrozenWireErrorForQueueSaturation()
    {
        string text = File.ReadAllText(ContractPath);

        Assert.Contains("existing protocol-v1 `internal_error` envelope", text, StringComparison.Ordinal);
        Assert.Contains("No new method, field, or error code", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokerContract_MakesOwnerEofFatalAndDiagnosticsContentFree()
    {
        string text = File.ReadAllText(ContractPath);

        Assert.Contains(
            "stdin EOF must terminate the broker even while model load is blocked",
            text,
            StringComparison.Ordinal);
        Assert.Contains("Cooperative cancellation is preferred", text, StringComparison.Ordinal);
        Assert.Contains("process-fatal exit is permitted", text, StringComparison.Ordinal);
        Assert.Contains("query text", text, StringComparison.Ordinal);
        Assert.Contains("document text", text, StringComparison.Ordinal);
        Assert.Contains("source text", text, StringComparison.Ordinal);
        Assert.Contains("workspace paths", text, StringComparison.Ordinal);
        Assert.Contains("symbols", text, StringComparison.Ordinal);
        Assert.Contains("snippets", text, StringComparison.Ordinal);
        Assert.Contains("vectors", text, StringComparison.Ordinal);
        Assert.Contains("authentication material", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SupersessionDocs_UseTheCompleteBrokerIdentityVocabulary()
    {
        string repo = ScaleTestSupport.RepoRoot();
        string[] paths =
        [
            Path.Combine(repo, "docs", "adr", "ADR-0003-semantic-retrieval-ownership.md"),
            Path.Combine(repo, "docs", "plans", "2026-07-19-miller-semantic-integration-design.md"),
            Path.Combine(repo, "docs", "plans", "2026-07-21-semantic-production-readiness-repair-design.md")
        ];

        foreach (string path in paths)
        {
            string text = File.ReadAllText(path);
            Assert.Contains("broker-contract/protocol/model identity", text, StringComparison.Ordinal);
            Assert.DoesNotContain("per protocol/model identity", text, StringComparison.Ordinal);
            Assert.DoesNotContain("deterministic protocol/model identity", text, StringComparison.Ordinal);
            Assert.DoesNotContain("same protocol/model identity", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BrokerContract_SeparatesFactoryRecoveryFromServiceBrokerArbitration()
    {
        string contract = File.ReadAllText(ContractPath);
        string design = File.ReadAllText(Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "docs",
            "plans",
            "2026-07-19-miller-semantic-integration-design.md"));

        Assert.Contains(
            "Miller owner recovery occurs through factory lifecycle",
            contract,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "recover ownership through the service-lock protocol",
            contract,
            StringComparison.Ordinal);
        Assert.Contains(
            "service lock arbitrates which sidecar service broker may load and serve",
            design,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "service lock arbitrates ownership",
            design,
            StringComparison.Ordinal);
    }
}
