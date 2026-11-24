using Azure.Monitor.OpenTelemetry.Exporter;
using Dan.ProxyV2;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((config) =>
    {
        config.AddJsonFile("host.json", optional: true);
        config.AddJsonFile("worker.json", optional: true);
    })
    .ConfigureLogging(logging =>
    {
        logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
        });

        // HttpClient's built-in logging writes the full target URL (path and query)
        // on every request. Only log warnings and above from it; outgoing calls are
        // still traced as dependencies.
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    })
    .ConfigureServices((context, services) =>
    {
        // UseAzureMonitorExporter() throws at startup if no connection string is configured,
        // so it's gated here to keep the proxy working locally without one.
        var appInsightsConnectionString = context.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
        {
            services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddHttpClientInstrumentation())
                .UseAzureMonitorExporter(options =>
                {
                    options.ConnectionString = appInsightsConnectionString;
                    // Export logs even when the trace they belong to is not sampled,
                    // so error logs are never silently dropped.
                    options.EnableTraceBasedLogsSampler = false;
                    // Disable sampling (default is rate-limited to 5 traces/sec) so all
                    // telemetry is exported, matching the previous Application Insights setup.
                    options.SamplingRatio = 1.0f;
                    options.TracesPerSecond = null;
                })
                .UseFunctionsWorkerDefaults();
        }

        services.AddDanProxy(context.Configuration);
    })
    .Build();

host.Run();
