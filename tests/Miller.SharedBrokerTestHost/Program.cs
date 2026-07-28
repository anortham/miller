using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Miller.Indexing.Semantic;

Dictionary<string, string> arguments = ParseArguments(args);
string endpoint = arguments["--endpoint"];
string lockPath = arguments["--lock"];
Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

FileStream serviceLock;
try
{
    serviceLock = new FileStream(
        lockPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);
}
catch (IOException)
{
    return;
}

await using (serviceLock)
{
    string counter = Environment.GetEnvironmentVariable("MILLER_FAKE_SHARED_BROKER_COUNTER")
        ?? throw new InvalidOperationException("missing fake broker counter");
    File.AppendAllText(counter, "loaded\n");
    int delay = int.TryParse(
        Environment.GetEnvironmentVariable("MILLER_FAKE_SHARED_BROKER_DELAY_MS"),
        out int parsed)
        ? parsed
        : 0;
    string? crashFirstMarker =
        Environment.GetEnvironmentVariable("MILLER_FAKE_SHARED_BROKER_CRASH_FIRST_MARKER");
    if (!string.IsNullOrWhiteSpace(crashFirstMarker))
    {
        try
        {
            await using FileStream marker = new(
                crashFirstMarker,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            await Task.Delay(delay);
            Environment.Exit(23);
        }
        catch (IOException)
        {
        }
    }
    if (Environment.GetEnvironmentVariable(
            "MILLER_FAKE_SHARED_BROKER_EXIT_ON_OWNER_CLOSE_DURING_DELAY") == "1")
    {
        await using Stream input = Console.OpenStandardInput();
        var buffer = new byte[1];
        Task ownerClosed = input.ReadAsync(buffer).AsTask();
        if (await Task.WhenAny(ownerClosed, Task.Delay(delay)) == ownerClosed)
            return;
        throw new InvalidOperationException(
            "owner-aware test delay elapsed before owner stdin closed");
    }
    await Task.Delay(delay);

    if (OperatingSystem.IsWindows())
    {
        await RunWindowsAsync(endpoint);
        return;
    }

    if (File.Exists(endpoint))
        File.Delete(endpoint);
    using var listener = new Socket(
        AddressFamily.Unix,
        SocketType.Stream,
        ProtocolType.Unspecified);
    listener.Bind(new UnixDomainSocketEndPoint(endpoint));
    listener.Listen(32);
    using var stop = new CancellationTokenSource();
    _ = WatchUnixOwnerAsync(stop, listener);
    try
    {
        while (!stop.IsCancellationRequested)
        {
            Socket socket = await listener.AcceptAsync(stop.Token);
            _ = ServeAsync(new NetworkStream(socket, ownsSocket: true), stop.Token);
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
    }
    catch (ObjectDisposedException) when (stop.IsCancellationRequested)
    {
    }
    finally
    {
        if (File.Exists(endpoint))
            File.Delete(endpoint);
    }
}

static async Task RunWindowsAsync(string endpoint)
{
    string prefix = @"\\.\pipe\";
    string pipeName = endpoint.StartsWith(prefix, StringComparison.Ordinal)
        ? endpoint[prefix.Length..]
        : endpoint;
    using var stop = new CancellationTokenSource();
    _ = WatchOwnerAsync(stop);
    try
    {
        while (!stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                32,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token);
                _ = ServeAsync(pipe, stop.Token);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
    }
}

static async Task WatchOwnerAsync(CancellationTokenSource stop)
{
    await using Stream input = Console.OpenStandardInput();
    var buffer = new byte[1];
    _ = await input.ReadAsync(buffer);
    stop.Cancel();
}

static async Task WatchUnixOwnerAsync(CancellationTokenSource stop, Socket listener)
{
    await using Stream input = Console.OpenStandardInput();
    var buffer = new byte[1];
    _ = await input.ReadAsync(buffer);
    stop.Cancel();
    listener.Dispose();
}

static async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
{
    using (stream)
    using (var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true))
    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
    {
        AutoFlush = true,
        NewLine = "\n",
    })
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    return;
                using JsonDocument request = JsonDocument.Parse(line);
                JsonElement id = request.RootElement.GetProperty("request_id").Clone();
                string method = request.RootElement.GetProperty("method").GetString()!;
                if (method == "health"
                    && int.TryParse(
                        Environment.GetEnvironmentVariable("MILLER_FAKE_SHARED_BROKER_HEALTH_DELAY_MS"),
                        out int healthDelay)
                    && healthDelay > 0)
                {
                    await Task.Delay(healthDelay, cancellationToken);
                }
                object result = method == "health" ? Health() : Embedding();
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    schema = SemanticEmbeddingSession.Schema,
                    version = SemanticEmbeddingSession.ProtocolVersion,
                    request_id = id,
                    result,
                }));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }
}

static object Health()
{
    SemanticEncoderPin pin = SemanticEncoderSelection.Active;
    return new
    {
        ready = true,
        dims = pin.Dims,
        model_id = pin.ModelId,
        model_sha256 = pin.ModelSha256,
        model_revision = pin.ModelRevision,
        pooling = pin.Pooling,
        normalization = "l2",
        resolved_backend = "cpu",
        accelerated = false,
        degraded_reason = Environment.GetEnvironmentVariable(
            "MILLER_FAKE_SHARED_BROKER_DEGRADED_REASON"),
    };
}

static object Embedding()
{
    int dims = SemanticEncoderSelection.Active.Dims;
    var vector = new float[dims];
    vector[0] = 1;
    return new { dims, vector };
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = Array.IndexOf(values, "broker") + 1; i + 1 < values.Length; i += 2)
        parsed[values[i]] = values[i + 1];
    return parsed;
}
