using System.Collections.Concurrent;
using System.Net.Sockets;
using Dan.ProxyV2.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace Dan.ProxyV2.UnitTest.TestSupport
{
    /// <summary>
    /// The proxy service wired up exactly as in Program.cs, pointed at a local test server.
    /// Settings are given by their configuration names, so the tests also cover the binding.
    /// </summary>
    internal sealed class ProxyFixture : IAsyncDisposable
    {
        public const string CertificateHeader = "x-client-certificate";

        private readonly ServiceProvider _services;
        private int _connectFailuresLeft;
        private int _connectHangsLeft;
        private int _connectAttempts;

        /// <param name="settings">Added to, or replacing, the defaults: the test certificate header and the test server as the only allowed host.</param>
        /// <param name="trustTestServer">
        /// False leaves the clients with normal certificate validation, which rejects the
        /// test server's self-signed certificate like any untrusted target.
        /// </param>
        /// <param name="failFirstConnects">
        /// When set, connection attempts are counted in <see cref="ConnectAttempts"/>, and this
        /// many of them fail before any succeeds.
        /// </param>
        /// <param name="hangFirstConnects">
        /// When set, connection attempts are counted in <see cref="ConnectAttempts"/>, and this
        /// many of them get no answer, as when a firewall drops the packets.
        /// </param>
        /// <param name="traceCalls">True traces the calls to targets with OpenTelemetry, as the app does in Azure.</param>
        public ProxyFixture(
            Dictionary<string, string?>? settings = null, bool trustTestServer = true, int? failFirstConnects = null, int? hangFirstConnects = null,
            bool traceCalls = false)
        {
            _connectFailuresLeft = failFirstConnects ?? 0;
            _connectHangsLeft = hangFirstConnects ?? 0;

            var values = new Dictionary<string, string?>
            {
                ["CustomCertificateHeaderName"] = CertificateHeader,
                ["AllowedTargetHosts"] = "127.0.0.1"
            };

            foreach (var setting in settings ?? [])
            {
                values[setting.Key] = setting.Value;
            }

            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(Logs));
            services.AddDanProxy(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
                handler =>
                {
                    if (trustTestServer)
                    {
                        TrustTestServer(handler);
                    }

                    if (failFirstConnects is not null || hangFirstConnects is not null)
                    {
                        CountAndFailConnects(handler);
                    }
                });

            if (traceCalls)
            {
                services.AddTargetCallTracing();
            }

            _services = services.BuildServiceProvider();
            Service = _services.GetRequiredService<IDanProxyService>();

            if (traceCalls)
            {
                // Without a host to start it, the tracing begins when the provider is first asked for.
                _services.GetRequiredService<TracerProvider>();
            }
        }

        public MtlsTestServer Server { get; } = new();

        public IDanProxyService Service { get; }

        public RecordingLoggerProvider Logs { get; } = new();

        /// <summary>Connection attempts made by the proxy. Only counted when failFirstConnects or hangFirstConnects is set.</summary>
        public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

        private void TrustTestServer(SocketsHttpHandler handler) =>
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, _, _) => Server.IsServerCertificate(certificate);

        /// <summary>
        /// The failing attempts throw what a failed name lookup throws on Linux, which the
        /// handler reports as "Resource temporarily unavailable". Later attempts connect normally.
        /// </summary>
        private void CountAndFailConnects(SocketsHttpHandler handler) =>
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                Interlocked.Increment(ref _connectAttempts);

                if (Interlocked.Decrement(ref _connectHangsLeft) >= 0)
                {
                    // Ends when the handler gives up the attempt.
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                if (Interlocked.Decrement(ref _connectFailuresLeft) >= 0)
                {
                    throw new SocketException((int)SocketError.TryAgain);
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };

        /// <param name="target">Host, port, path and query without scheme. Defaults to a path on the test server.</param>
        public FakeHttpRequestData CreateRequest(string? target = null, string method = "GET", string? certificate = null, byte[]? body = null)
        {
            var encodedTarget = Uri.EscapeDataString(target ?? $"{Server.Authority}/some/path");
            var request = new FakeHttpRequestData(new Uri($"https://proxy.test/?url={encodedTarget}"), method, body);

            if (certificate is not null)
            {
                request.Headers.Add(CertificateHeader, certificate);
            }

            return request;
        }

        public async Task<FakeHttpResponseData> SendAsync(FakeHttpRequestData request, CancellationToken cancellationToken = default) =>
            (FakeHttpResponseData)await Service.ProxyRequest(request, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    /// <summary>Collects every formatted log message, so tests can check what ends up in the log.</summary>
    internal sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
