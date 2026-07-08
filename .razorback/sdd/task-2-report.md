# Task 2 Report: Dashboard error logging

**Status:** DONE
**implementation commit SHA:** 176788d

## Summary

Wired `Microsoft.Extensions.Logging` console provider (Information minimum) into the dashboard `HostBuilder` and added leading exception-handling middleware that logs at Error and returns a plain-text 500 body. Dashboard Release build: 0 warnings, 0 errors.

## Miller calls

1. **`context(query='Miller.Dashboard Program host builder logging')`**
   - Confirmed dashboard host is bare `HostBuilder` with no logging setup; Server uses separate Serilog path

2. **`inspect(target='src/Miller.Dashboard/Program.cs', depth='full')`**
   - 90-line top-level Program; `HostBuilder` at :24–:84
   - No existing logging or middleware

## API-shape evidence

### After (Program.cs)
```csharp
.ConfigureLogging(logging =>
    logging.AddSimpleConsole()
        .SetMinimumLevel(LogLevel.Information))
// ...
var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Miller.Dashboard");
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled dashboard request exception");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            $"miller-dashboard error: {ex.GetType().Name}: {ex.Message}");
    }
});
app.UseRouting();
```

- Logging wired on `webBuilder` before Kestrel/content-root/URLs
- Middleware is first in pipeline (before `UseRouting`)
- No developer exception page
- No Serilog; only `AddSimpleConsole()`

## Judgment calls

| Decision | Rationale |
|----------|-----------|
| Resolve logger via `ILoggerFactory.CreateLogger("Miller.Dashboard")` | Explicit stable category; satisfies "resolve ILogger from ApplicationServices" |
| Logger captured once at `Configure` time | Avoids per-request factory lookup |
| Default `AddSimpleConsole()` options | Minimal per brief |
| No `Response.HasStarted` guard | Brief specifies minimal try/catch; Task 6 covers live paths |

## Files changed

| File | Change |
|------|--------|
| `src/Miller.Dashboard/Program.cs` | +4 usings; `ConfigureLogging`; exception middleware before routing |

## Verification

| Scope | Invariant | Command | Commit | Result | Time |
|-------|-----------|---------|--------|--------|------|
| worker-red-green | logging + middleware compile 0/0 | `dotnet build src/Miller.Dashboard/Miller.Dashboard.csproj -c Release` | none | PASS 0/0 | Task 2 |

## Acceptance criteria

- [x] Dashboard build succeeds with 0 warnings
- [x] Logging pipeline wired (`AddSimpleConsole`, Information minimum)
- [x] Exception middleware logs Error + plain-text 500 body shape implemented
- [x] Startup lifetime logs emitted during Task 6 live verification

## Concerns

None. Host wiring intentionally not unit-tested per plan; Task 6 provides live evidence.
