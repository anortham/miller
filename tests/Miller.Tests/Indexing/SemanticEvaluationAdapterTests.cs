using System.Text.Json;
using System.Text.Json.Nodes;
using Miller.Indexing;
using Miller.Indexing.Semantic;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SemanticEvaluationAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "miller-semantic-evaluation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CodeRankEncoder_IsTheFrozenCurrentJulieProducerIdentity()
    {
        SemanticEncoderPin pin = SemanticEvaluationAdapter.CodeRankEncoder;

        Assert.Equal("nomic-ai/CodeRankEmbed", pin.ModelId);
        Assert.Equal("827529bcd58aef0d9082e66eeff7e7d53a02f62bd005f841a26b3d3e2fb17ebe", pin.ModelSha256);
        Assert.Equal("3c4b60807d71f79b43f3c4363786d9493691f8b1", pin.ModelRevision);
        Assert.Equal(768, pin.Dims);
        Assert.Equal("cls", pin.Pooling);
        Assert.Equal("", pin.EosAppend);
        Assert.Equal("", pin.QueryInstruction);
        Assert.Equal("", pin.DocumentInstruction);
        Assert.Equal("vec0-int8-768-cosine-v1", pin.StorageSchema);
        Assert.Equal(
            "sha256:d8dc59f24eba7660b4ced421b3d89d54d8d7c71a143f8dfa47dc72c43c9a4b1c",
            MillerSemanticContract.EncoderFingerprint(pin));
    }

    [Fact]
    public void Load_RequiresTheExactFrozenPinBeforeConstructingAProducer()
    {
        string path = WriteConfig();

        SemanticEvaluationAdapter adapter = SemanticEvaluationAdapter.Load(path);

        Assert.Equal(SemanticEvaluationAdapter.CodeRankEncoder, adapter.Encoder);
        Assert.Equal("/opt/eval/python", adapter.ProducerExecutable);
        Assert.Equal(["-m", "sidecar.main"], adapter.ProducerArguments);
        Assert.Equal("1", adapter.ProducerEnvironment["TRANSFORMERS_OFFLINE"]);
        Assert.Equal(
            MillerSemanticContract.PinnedIdentity(SemanticEvaluationAdapter.CodeRankEncoder),
            adapter.GenerationIdentity);
        Assert.Equal(MillerSemanticContract.FusionProfile, adapter.GenerationIdentity.FusionProfile);
        Assert.Equal(MillerSemanticContract.CorpusGeneration, adapter.GenerationIdentity.CorpusGeneration);
    }

    [Theory]
    [InlineData("model_id", "other-model")]
    [InlineData("model_sha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("model_revision", "main")]
    [InlineData("dims", 384)]
    [InlineData("pooling", "mean")]
    [InlineData("eos_append", "</s>")]
    [InlineData("query_instruction", "Represent this query: ")]
    [InlineData("document_instruction", "Represent this document: ")]
    [InlineData("storage_schema", "vec0-int8-384-cosine-v1")]
    public void Load_RefusesEveryEmbeddingAffectingPinDisagreement(string field, object replacement)
    {
        string path = WriteConfig((encoder, _) =>
        {
            encoder[field] = replacement switch
            {
                string value => JsonValue.Create(value),
                int value => JsonValue.Create(value),
                _ => throw new ArgumentOutOfRangeException(nameof(replacement)),
            };
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => SemanticEvaluationAdapter.Load(path));

        Assert.Contains(field, error.Message);
        Assert.False(File.Exists(Path.Combine(_root, "launched")));
    }

    [Fact]
    public void Load_RefusesAnUnpinnedNormalizationPolicy()
    {
        string path = WriteConfig((_, root) =>
        {
            root["normalization"] = JsonValue.Create("none");
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => SemanticEvaluationAdapter.Load(path));

        Assert.Contains("normalization", error.Message);
    }

    [Fact]
    public void Load_RefusesUnknownFieldsInsteadOfIgnoringAProducerTypo()
    {
        string path = WriteConfig((_, root) =>
        {
            root["producer"]!.AsObject()["argumants"] = JsonSerializer.SerializeToNode(new[] { "--wrong" });
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => SemanticEvaluationAdapter.Load(path));

        Assert.Contains("argumants", error.Message);
    }

    [Fact]
    public void Load_RefusesDuplicateFieldsInsteadOfUsingJsonLastWriteWins()
    {
        string path = WriteConfig();
        string source = File.ReadAllText(path);
        File.WriteAllText(
            path,
            source.Replace(
                "\"version\": 1,",
                "\"version\": 1,\n  \"version\": 1,",
                StringComparison.Ordinal));

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => SemanticEvaluationAdapter.Load(path));

        Assert.Contains("duplicate", error.Message);
        Assert.Contains("version", error.Message);
    }

    [Fact]
    public void Disabled_DoesNotReadTheEvaluationConfigOrTouchAVectorPath()
    {
        string missing = Path.Combine(_root, "missing.json");
        string workspace = Path.Combine(_root, "workspace");

        SemanticEvaluationAdapter? adapter =
            SemanticEvaluationAdapter.LoadWhenEnabled(SemanticMode.Off, missing);

        Assert.Null(adapter);
        Assert.False(Directory.Exists(workspace));
        Assert.False(File.Exists(VectorSidecar.PathFor(workspace)));
    }

    [Fact]
    public async Task CreateSession_UsesTheInjectedExpectedPinAndPreservesFallbackTruth()
    {
        string path = WriteConfig();
        SemanticEvaluationAdapter adapter = SemanticEvaluationAdapter.Load(path);
        var health = new SemanticSidecarHealth(
            Ready: true,
            Dims: adapter.Encoder.Dims,
            ModelId: adapter.Encoder.ModelId,
            ModelSha256: adapter.Encoder.ModelSha256,
            ModelRevision: adapter.Encoder.ModelRevision,
            Pooling: adapter.Encoder.Pooling,
            Normalization: "l2",
            ResolvedBackend: "cpu",
            Accelerated: false,
            DegradedReason: "requested mps backend is unavailable");
        await using SemanticEmbeddingSession session = adapter.CreateSession(
            new SingleHealthLauncher(health));

        SemanticEncoderHandshake? handshake =
            await session.EnsureStartedAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(handshake);
        Assert.Equal("cpu", handshake.ResolvedBackend);
        Assert.False(handshake.Accelerated);
        Assert.Equal("requested mps backend is unavailable", handshake.DegradedReason);
    }

    [Theory]
    [InlineData("dims")]
    [InlineData("model_id")]
    [InlineData("model_sha256")]
    [InlineData("model_revision")]
    [InlineData("pooling")]
    [InlineData("normalization")]
    public void MatchEncoder_RefusesEveryHandshakeVisibleCodeRankMismatch(string field)
    {
        SemanticEncoderPin pin = SemanticEvaluationAdapter.CodeRankEncoder;
        var health = new SemanticSidecarHealth(
            Ready: true,
            Dims: pin.Dims,
            ModelId: pin.ModelId,
            ModelSha256: pin.ModelSha256,
            ModelRevision: pin.ModelRevision,
            Pooling: pin.Pooling,
            Normalization: SemanticEvaluationAdapter.Normalization,
            ResolvedBackend: "cpu",
            Accelerated: false,
            DegradedReason: null);
        health = field switch
        {
            "dims" => health with { Dims = 384 },
            "model_id" => health with { ModelId = "other-model" },
            "model_sha256" => health with { ModelSha256 = new string('a', 64) },
            "model_revision" => health with { ModelRevision = "main" },
            "pooling" => health with { Pooling = "mean" },
            "normalization" => health with { Normalization = "none" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        SemanticEncoderHandshake? handshake =
            SemanticEmbeddingSession.MatchEncoder(health, pin, out string? refusalReason);

        Assert.Null(handshake);
        Assert.Contains(field, refusalReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoEvaluationClients_KeepConcurrentProtocolStreamsIsolated()
    {
        SemanticEncoderPin pin = SemanticEvaluationAdapter.CodeRankEncoder;
        await using var first = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(encoder: pin),
            expectedEncoder: pin);
        await using var second = new SemanticEmbeddingSession(
            FakeSemanticSidecar.InProcessLauncher(encoder: pin),
            expectedEncoder: pin);

        Task<SemanticEmbedOutcome> firstRequest =
            first.EmbedBatchAsync(["first client"], TestContext.Current.CancellationToken);
        Task<SemanticEmbedOutcome> secondRequest =
            second.EmbedBatchAsync(["second client"], TestContext.Current.CancellationToken);
        await Task.WhenAll(firstRequest, secondRequest);

        SemanticEmbedOutcome firstOutcome = await firstRequest;
        SemanticEmbedOutcome secondOutcome = await secondRequest;
        Assert.True(firstOutcome.Succeeded, firstOutcome.FailureReason);
        Assert.True(secondOutcome.Succeeded, secondOutcome.FailureReason);
        Assert.Equal(
            FakeSemanticSidecar.ExpectedVector("document", "first client", pin.Dims),
            Assert.Single(firstOutcome.Vectors));
        Assert.Equal(
            FakeSemanticSidecar.ExpectedVector("document", "second client", pin.Dims),
            Assert.Single(secondOutcome.Vectors));
        Assert.NotEqual(firstOutcome.Vectors[0], secondOutcome.Vectors[0]);
    }

    [Fact]
    [Trait("Category", "Scale")]
    public async Task ConfiguredCodeRankProducer_HandshakesAndEmbedsOnTheActualRuntime()
    {
        string? configPath = Environment.GetEnvironmentVariable("MILLER_CODERANK_EVALUATION_CONFIG");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(configPath),
            "Set MILLER_CODERANK_EVALUATION_CONFIG to an explicit evaluator-only CodeRank runtime config.");
        SemanticEvaluationAdapter adapter = SemanticEvaluationAdapter.Load(configPath!);
        await using SemanticEmbeddingSession session = adapter.CreateSession();

        SemanticEncoderHandshake? handshake =
            await session.EnsureStartedAsync(TestContext.Current.CancellationToken);
        SemanticEmbedOutcome outcome =
            await session.EmbedQueryAsync("find the workspace refresh coordinator", TestContext.Current.CancellationToken);

        Assert.NotNull(handshake);
        Assert.Equal(adapter.Encoder.Dims, handshake.Dims);
        Assert.True(outcome.Succeeded, outcome.FailureReason);
        Assert.Equal(adapter.Encoder.Dims, Assert.Single(outcome.Vectors).Length);
    }

    [Fact]
    public void Evidence_RecordsTheArmIdentityWithoutProducerSecrets()
    {
        string path = WriteConfig((_, root) =>
        {
            JsonObject producer = root["producer"]!.AsObject();
            JsonObject environment = producer["environment"]!.AsObject();
            environment["HF_TOKEN"] = JsonSerializer.SerializeToNode("secret-value");
        });
        SemanticEvaluationAdapter adapter = SemanticEvaluationAdapter.Load(path);
        string evidencePath = Path.Combine(_root, "evidence.json");

        adapter.WriteEvidence(evidencePath);

        using JsonDocument evidence = JsonDocument.Parse(File.ReadAllText(evidencePath));
        JsonElement root = evidence.RootElement;
        Assert.Equal("coderank-current-julie", root.GetProperty("arm_id").GetString());
        Assert.Equal(
            MillerSemanticContract.EncoderFingerprint(adapter.Encoder),
            root.GetProperty("encoder_fingerprint").GetString());
        Assert.Equal(adapter.Encoder.StorageSchema, root.GetProperty("storage_schema").GetString());
        Assert.Equal(MillerSemanticContract.CorpusGeneration, root.GetProperty("corpus_generation").GetString());
        Assert.Equal(MillerSemanticContract.FusionProfile, root.GetProperty("fusion_profile").GetString());
        Assert.DoesNotContain("secret-value", File.ReadAllText(evidencePath));
    }

    private string WriteConfig(Action<JsonObject, JsonObject>? mutate = null)
    {
        Directory.CreateDirectory(_root);
        JsonObject encoder = JsonSerializer.SerializeToNode(new
        {
            model_id = "nomic-ai/CodeRankEmbed",
            model_sha256 = "827529bcd58aef0d9082e66eeff7e7d53a02f62bd005f841a26b3d3e2fb17ebe",
            model_revision = "3c4b60807d71f79b43f3c4363786d9493691f8b1",
            dims = 768,
            pooling = "cls",
            eos_append = "",
            query_instruction = "",
            document_instruction = "",
            storage_schema = "vec0-int8-768-cosine-v1",
        })!.AsObject();
        JsonObject root = JsonSerializer.SerializeToNode(new
        {
            schema = "miller.semantic.evaluation-adapter",
            version = 1,
            arm_id = "coderank-current-julie",
            normalization = "l2",
            encoder,
            producer = new
            {
                executable = "/opt/eval/python",
                arguments = new[] { "-m", "sidecar.main" },
                environment = new Dictionary<string, string>
                {
                    ["TRANSFORMERS_OFFLINE"] = "1",
                },
            },
        })!.AsObject();
        mutate?.Invoke(root["encoder"]!.AsObject(), root);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed class SingleHealthLauncher(SemanticSidecarHealth health)
        : ISemanticSidecarConnectionFactory
    {
        public ValueTask<ISemanticSidecarConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string response = JsonSerializer.Serialize(new
            {
                schema = SemanticEmbeddingSession.Schema,
                version = SemanticEmbeddingSession.ProtocolVersion,
                request_id = "1",
                result = new
                {
                    ready = health.Ready,
                    dims = health.Dims,
                    model_id = health.ModelId,
                    model_sha256 = health.ModelSha256,
                    model_revision = health.ModelRevision,
                    pooling = health.Pooling,
                    normalization = health.Normalization,
                    resolved_backend = health.ResolvedBackend,
                    accelerated = health.Accelerated,
                    degraded_reason = health.DegradedReason,
                },
            });
            return ValueTask.FromResult<ISemanticSidecarConnection>(
                new SingleHealthConnection(response));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleHealthConnection(string response) : ISemanticSidecarConnection
    {
        private readonly StringWriter _input = new();
        private readonly StringReader _output = new(response + Environment.NewLine);

        public TextWriter Input => _input;

        public TextReader Output => _output;

        public bool IsClosed => false;

        public void Abort()
        {
        }

        public ValueTask DisposeAsync()
        {
            _input.Dispose();
            _output.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
