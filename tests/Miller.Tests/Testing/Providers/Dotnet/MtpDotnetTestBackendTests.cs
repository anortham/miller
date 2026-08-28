using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Dotnet;

public sealed class MtpDotnetTestBackendTests
{
    [Fact]
    public void BuildInfoCommand_uses_the_built_test_module_and_no_workspace_source_paths()
    {
        var command = MtpDotnetTestBackend.BuildInfoCommand(
            "dotnet",
            "/tmp/generation/out/VbMtpScale/VbMtpScale.dll",
            "/tmp/repo");

        Assert.Equal("dotnet", command.FileName);
        Assert.Equal(
            ["exec", "/tmp/generation/out/VbMtpScale/VbMtpScale.dll", "--info"],
            command.Arguments);
        Assert.Equal("/tmp/repo", command.WorkingDirectory);
    }

    [Fact]
    public void BuildDiscoverCommand_uses_version_appropriate_machine_list_output()
    {
        var command = MtpDotnetTestBackend.BuildDiscoverCommand(
            "dotnet",
            "/tmp/generation/out/VbMtpScale/VbMtpScale.dll",
            "/tmp/repo",
            new MtpVersion(2, 3, 0));

        Assert.Equal(
            ["exec", "/tmp/generation/out/VbMtpScale/VbMtpScale.dll", "--no-banner", "--list-tests", "json"],
            command.Arguments);
    }

    [Fact]
    public void BuildRunCommand_refuses_an_unproven_framework_filter()
    {
        Assert.Throws<ContinuousTestProviderException>(() => MtpDotnetTestBackend.BuildRunCommand(
            "dotnet",
            "/tmp/generation/out/VbMtpScale/VbMtpScale.dll",
            "/tmp/repo",
            new MtpVersion(2, 3, 0),
            "/tmp/generation/results/run.trx",
            "mstest",
            ["mstest:VbMtpScale.UnitTests.Adds"],
            wholeSuite: false,
            filterCapabilityProven: false));
    }

    [Fact]
    public void BuildRunCommand_keeps_whole_suite_unfiltered_and_trx_generation_scoped()
    {
        var command = MtpDotnetTestBackend.BuildRunCommand(
            "dotnet",
            "/tmp/generation/out/VbMtpScale/VbMtpScale.dll",
            "/tmp/repo",
            new MtpVersion(2, 3, 0),
            "/tmp/generation/results/run.trx",
            "mstest",
            [],
            wholeSuite: true,
            filterCapabilityProven: true);

        Assert.DoesNotContain("--filter", command.Arguments);
        Assert.Contains("--report-trx", command.Arguments);
        Assert.Contains("/tmp/generation/results", command.Arguments);
    }

    [Fact]
    public void ParseTrxResult_rejects_malformed_or_missing_selected_results()
    {
        Assert.Throws<ContinuousTestProviderException>(() => MtpDotnetTestBackend.ParseTrxResult(
            "<TestRun>",
            "mstest",
            "rev-1",
            "identity-1",
            ["mstest:Sample.Tests.Adds"],
            "/tmp/generation/results/run.trx"));

        var exception = Assert.Throws<ContinuousTestProviderException>(() => MtpDotnetTestBackend.ParseTrxResult(
            "<TestRun><Results /></TestRun>",
            "mstest",
            "rev-1",
            "identity-1",
            ["mstest:Sample.Tests.Adds"],
            "/tmp/generation/results/run.trx"));
        Assert.Contains("selected", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseTrxResult_reads_the_mtp_unit_test_definition_shape()
    {
        const string path = "/tmp/generation/results/run.trx";
        var result = MtpDotnetTestBackend.ParseTrxResult(
            """
            <TestRun id="run-1" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Times start="2026-08-28T12:00:00.0000000Z" finish="2026-08-28T12:00:00.1000000Z" />
              <Results>
                <UnitTestResult executionId="execution-1" testId="definition-1" testName="Adds" outcome="Passed" duration="00:00:00.0100000" />
              </Results>
              <TestDefinitions>
                <UnitTest name="Adds" id="definition-1">
                  <TestMethod className="VbMtpScale.UnitTests" name="Adds" />
                </UnitTest>
              </TestDefinitions>
            </TestRun>
            """,
            "mstest",
            "rev-1",
            "identity-1",
            ["mstest:VbMtpScale.UnitTests.Adds"],
            path);

        var testCase = Assert.Single(result.CaseResults);
        Assert.Equal("mstest:VbMtpScale.UnitTests.Adds", testCase.TestCaseId);
        Assert.Equal("passed", testCase.Status);
    }
}
