namespace Miller.Server.Telemetry;

public sealed record TelemetryHealthFacts(long OkCount, long EmptyCount, long ErrorCount)
{
    public long TotalCalls => OkCount + EmptyCount + ErrorCount;
}
