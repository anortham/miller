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
}
