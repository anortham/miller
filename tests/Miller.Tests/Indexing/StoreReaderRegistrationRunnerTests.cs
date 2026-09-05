using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreReaderRegistrationRunnerTests
{
    [Fact]
    public async Task Invalid_utf8_is_rejected_without_rewriting_identity_bytes()
    {
        using var stream = new MemoryStream([0xc3, 0x28]);
        await Assert.ThrowsAsync<StoreReaderRegistrationException>(() =>
            JulieStoreClient.ReadReaderOutputAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Traversing_family_root_refuses_before_transport()
    {
        int calls = 0;
        var runner = new StoreReaderRegistrationRunner((_, _) => { calls++; return new(0, Report, ""); });
        ReaderAcquireRequest request = Request();
        request = request with { Binding = request.Binding with { StoreRoot = Path.Combine(Path.GetTempPath(), "family", "..", "escape") } };
        Assert.Throws<StoreReaderRegistrationException>(() => runner.Acquire(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Missing_executable_returns_no_credential_or_binary_path_diagnostic()
    {
        var client = new JulieStoreClient("missing-" + Nonce);
        var error = Assert.Throws<StoreReaderRegistrationException>(() => client.InvokeReader(["store", "reader", "acquire"], TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Nonce, error.ToString());
        Assert.False(error.MayHaveAcquired);
    }

    [Fact]
    public async Task Process_capture_rejects_output_above_byte_budget()
    {
        using var stream = new MemoryStream(new byte[65537]);
        await Assert.ThrowsAsync<StoreReaderRegistrationException>(() =>
            JulieStoreClient.ReadReaderOutputAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Canceled_reader_invocation_does_not_start_a_process()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var client = new JulieStoreClient("not-a-real-executable");
        Assert.ThrowsAny<OperationCanceledException>(() => client.InvokeReader(["store", "reader", "acquire"], cancel.Token));
    }

    internal const string Nonce = "01234567890123456789012345678901";
    internal const string Family = "11111111-1111-1111-1111-111111111111";
    internal static ReaderAcquireRequest Request() => new(
        new StoreFamilyBinding(Guid.Parse(Family), Path.GetTempPath(), "view-42", Path.GetTempPath(), StoreBindingState.Ready),
        "gen-000042", "miller", 1234, Nonce);

    internal const string Report = """
        {"report_schema_version":1,"operation":"reader_acquire","state":"acquired",
        "family_id":"11111111-1111-1111-1111-111111111111","view_id":"view-42","pin_id":"pin-42",
        "generation_name":"gen-000042","manifest_generation":42,"owner_nonce":"01234567890123456789012345678901",
        "owner_pid":1234,"store_instance_id":"11111111-1111-1111-1111-111111111111:gen-000042",
        "manifest_hash":"manifest-hash-42","extraction_identity_epoch":7,"served_store_log_sequence":100,
        "min_retained_store_log_sequence":80,"snapshot_fingerprint":"e91d2df2fbbf1916fad02a6c0acfc7c9842370d1a003cb0782325ed769c5af1a",
        "protected_manifest_count":1,"expires_at":1900000120000,"warning":null,"failure_class":null,"error":null}
        """;

    [Fact]
    public void Acquire_validates_complete_producer_identity()
    {
        ReaderAcquireResult result = StoreReaderRegistrationRunner.ParseAcquire(new(0, Report, ""), Request());
        Assert.Equal(42, result.Snapshot.ManifestGeneration);
        Assert.Equal(1, result.Snapshot.ProtectedManifestCount);
        Assert.Equal(80, result.Snapshot.MinRetainedStoreLogSequence);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1900000120000), result.ExpiresAt);
        Assert.DoesNotContain(Nonce, result.ToString());
        Assert.DoesNotContain("pin-42", result.ToString());
        Assert.DoesNotContain(Nonce, Request().ToString());
    }

    [Theory]
    [InlineData("\"manifest_hash\":\"manifest-hash-42\",", "")]
    [InlineData("\"report_schema_version\":1", "\"report_schema_version\":2")]
    [InlineData("\"owner_pid\":1234", "\"owner_pid\":1234,\"owner_pid\":1234")]
    [InlineData("gen-000042", "../gen-000042")]
    [InlineData("\"protected_manifest_count\":1", "\"protected_manifest_count\":0")]
    [InlineData("\"extraction_identity_epoch\":7", "\"extraction_identity_epoch\":8")]
    [InlineData("\"min_retained_store_log_sequence\":80", "\"min_retained_store_log_sequence\":101")]
    [InlineData("\"owner_pid\":1234", "\"owner_pid\":5678")]
    [InlineData("\"operation\":\"reader_acquire\"", "\"operation\":\"reader_renew\"")]
    public void Invalid_reports_refuse_without_echoing_credentials(string original, string replacement)
    {
        var error = Assert.Throws<StoreReaderRegistrationException>(() =>
            StoreReaderRegistrationRunner.ParseAcquire(new(0, Report.Replace(original, replacement), Nonce), Request()));
        Assert.DoesNotContain(Nonce, error.ToString());
        Assert.DoesNotContain("pin-42", error.ToString());
    }

    [Fact]
    public void Oversized_output_refuses_before_parsing()
    {
        Assert.Throws<StoreReaderRegistrationException>(() =>
            StoreReaderRegistrationRunner.ParseAcquire(new(0, Report + new string(' ', 65536), ""), Request()));
    }

    [Fact]
    public void Incompatible_producer_is_a_typed_refusal()
    {
        var error = Assert.Throws<StoreReaderRegistrationException>(() => StoreReaderRegistrationRunner.ParseAcquire(
            new(3, "{\"report_schema_version\":1,\"operation\":\"reader_acquire\",\"state\":\"refused\",\"failure_class\":\"incompatible_store\",\"error\":\"store is incompatible\"}", ""), Request()));
        Assert.Equal(ReaderFailure.Incompatible, error.Failure);
    }

    [Fact]
    public void Lost_reply_retries_identical_nonce_and_generation()
    {
        var calls = new List<string[]>();
        var runner = new StoreReaderRegistrationRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            return calls.Count == 1 ? new(null, "", "", TransportLost: true) : new(0, Report, "");
        });
        Assert.Equal(42, runner.Acquire(Request(), TestContext.Current.CancellationToken).Snapshot.ManifestGeneration);
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0], calls[1]);
        Assert.Contains(Nonce, calls[0]);
        Assert.Contains("120000", calls[0]);
        Assert.Contains("1234", calls[0]);
    }

    [Fact]
    public void Ambiguous_retries_are_bounded_and_cancellation_is_observed_between_attempts()
    {
        int calls = 0;
        var runner = new StoreReaderRegistrationRunner((_, _) => { calls++; return new(null, "", "", TransportLost: true); });
        var error = Assert.Throws<StoreReaderRegistrationException>(() => runner.Acquire(Request(), TestContext.Current.CancellationToken));
        Assert.True(error.MayHaveAcquired);
        Assert.Equal(3, calls);
        using var cancel = new CancellationTokenSource();
        runner = new((_, _) => { cancel.Cancel(); return new(null, "", "", TransportLost: true); });
        Assert.ThrowsAny<OperationCanceledException>(() => runner.Acquire(Request(), cancel.Token));
    }
}
