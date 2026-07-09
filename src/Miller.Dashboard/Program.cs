using Microsoft.Extensions.Logging;
using Miller.Dashboard;

DashboardPaths paths = DashboardPaths.FromEnvironment(AppContext.BaseDirectory);
string launchDirectory = Environment.GetEnvironmentVariable("MILLER_DASHBOARD_PREFERRED_ROOT")
    ?? Environment.CurrentDirectory;

// Services + middleware + endpoints live in DashboardHostPipeline so HTTP-level tests run the
// exact production pipeline on TestServer; only Kestrel/URL/logging concerns stay here.
var host = new HostBuilder()
    .ConfigureWebHost(webBuilder =>
    {
        webBuilder
            .ConfigureLogging(logging =>
                logging.AddSimpleConsole()
                    .SetMinimumLevel(LogLevel.Information))
            .UseKestrel()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseUrls(paths.Url)
            .ConfigureServices(DashboardHostPipeline.ConfigureServices)
            .Configure(app => DashboardHostPipeline.Configure(app, paths, launchDirectory));
    })
    .Build();

host.Run();
