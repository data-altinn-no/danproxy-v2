using Dan.ProxyV2.Config;
using Dan.ProxyV2.Interfaces;
using Dan.ProxyV2.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Trace;

namespace Dan.ProxyV2
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>Registers the proxy service, its settings and the HTTP clients it calls targets with.</summary>
        public static IServiceCollection AddDanProxy(this IServiceCollection services, IConfiguration configuration) =>
            services.AddDanProxy(configuration, configureTargetHandler: null);

        // Lets tests adjust the handlers, for example to trust a local test server.
        internal static IServiceCollection AddDanProxy(
            this IServiceCollection services, IConfiguration configuration, Action<SocketsHttpHandler>? configureTargetHandler)
        {
            services.Configure<DanProxySettings>(configuration);
            services.AddTransient<IDanProxyService, DanProxyService>();
            services.AddSingleton<IClientCertificateHttpClientProvider>(provider => new ClientCertificateHttpClientProvider(
                provider.GetRequiredService<IOptions<DanProxySettings>>().Value, configureTargetHandler));

            services.AddHttpClient(Constants.DanProxyHttpClient)
                .ConfigurePrimaryHttpMessageHandler(provider => TargetHttpHandler.Create(
                    provider.GetRequiredService<IOptions<DanProxySettings>>().Value, configureTargetHandler))
                .AsLongLivedTargetClient();

            services.AddHttpClient(Constants.DanProxyNoValidationHttpClient)
                .ConfigurePrimaryHttpMessageHandler(provider => TargetHttpHandler.CreateWithoutCertificateValidation(
                    provider.GetRequiredService<IOptions<DanProxySettings>>().Value, configureTargetHandler))
                .AsLongLivedTargetClient();

            return services;
        }

        /// <summary>Traces the calls to targets, so they are reported as dependencies.</summary>
        internal static OpenTelemetryBuilder AddTargetCallTracing(this IServiceCollection services)
        {
            // The tracing writes trace headers on every outgoing call with this propagator. It
            // does so past the handler, whose own propagator already writes nothing. With one
            // that writes nothing here as well, the proxy's trace ids stay in our own telemetry
            // and are not sent to other organisations.
            Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([]));

            return services.AddOpenTelemetry().WithTracing(tracing => tracing.AddHttpClientInstrumentation());
        }

        private static IHttpClientBuilder AsLongLivedTargetClient(this IHttpClientBuilder builder) =>
            builder
                // The factory would otherwise replace the handler, and with it every open connection,
                // every two minutes. The handler recycles its own connections to pick up DNS changes.
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
                // The time limit is set per request by the proxy service.
                .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
    }
}
