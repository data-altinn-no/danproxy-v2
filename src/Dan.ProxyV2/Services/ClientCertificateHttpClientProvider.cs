using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Dan.ProxyV2.Config;
using Dan.ProxyV2.Interfaces;

namespace Dan.ProxyV2.Services
{
    /// <summary>
    /// Keeps one HttpClient per certificate header value for as long as the value is in use.
    ///
    /// Creating a new HttpClient and handler per request gives every request an empty
    /// connection pool, so each call pays for a DNS lookup, a TCP connect and a TLS
    /// handshake, and leaves a socket behind until it is collected. Sharing the handler
    /// lets requests with the same header value reuse open connections.
    ///
    /// Each header value gets its own handler, and therefore its own connection pool, so a
    /// connection authenticated with one certificate is never used for a request that
    /// carries another.
    ///
    /// Callers must send exactly the same header value on every request to get reuse. A PFX
    /// that is exported again is a different value, because every export contains new random
    /// salt. The cache is therefore limited to <see cref="MaxClients"/> clients, so a caller
    /// that sends a new value on every request cannot make it grow without bound.
    ///
    /// A client that has not been used for <see cref="UnusedClientLifetime"/> is dropped.
    /// </summary>
    public sealed class ClientCertificateHttpClientProvider : IClientCertificateHttpClientProvider, IDisposable
    {
        internal const int MaxClients = 20;

        // A client that has not been used for this long is dropped, so the private key of a
        // certificate that has been replaced does not stay in memory for as long as the process lives.
        internal static readonly TimeSpan UnusedClientLifetime = TimeSpan.FromHours(1);

        // How often the cache is searched for unused clients.
        internal static readonly TimeSpan UnusedClientCheckInterval = TimeSpan.FromMinutes(5);

        private readonly ConcurrentDictionary<string, CachedClient> _clients = new();
        private readonly DanProxySettings _settings;
        private readonly Action<SocketsHttpHandler>? _configureHandler;
        private readonly TimeProvider _timeProvider;
        private readonly ITimer _unusedClientTimer;
        private long _useCounter;

        public ClientCertificateHttpClientProvider() : this(new DanProxySettings(), null)
        {
        }

        public ClientCertificateHttpClientProvider(DanProxySettings settings) : this(settings, null)
        {
        }

        // Lets tests adjust the handler, for example to trust a local test server.
        internal ClientCertificateHttpClientProvider(Action<SocketsHttpHandler>? configureHandler) : this(new DanProxySettings(), configureHandler)
        {
        }

        internal ClientCertificateHttpClientProvider(DanProxySettings settings, Action<SocketsHttpHandler>? configureHandler)
            : this(settings, configureHandler, TimeProvider.System)
        {
        }

        // Lets tests move the clock.
        internal ClientCertificateHttpClientProvider(DanProxySettings settings, Action<SocketsHttpHandler>? configureHandler, TimeProvider timeProvider)
        {
            _settings = settings;
            _configureHandler = configureHandler;
            _timeProvider = timeProvider;
            _unusedClientTimer = timeProvider.CreateTimer(
                _ => DropUnusedClients(), state: null, UnusedClientCheckInterval, UnusedClientCheckInterval);
        }

        internal int CachedClientCount => _clients.Count;

        public HttpClient GetClient(string base64Certificate)
        {
            // Keyed on a hash of the header value, so the certificate is only parsed the
            // first time it is seen and the private key is not kept as a dictionary key.
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(base64Certificate)));

            if (!_clients.TryGetValue(key, out var cached))
            {
                // Created before anything is dropped, so a header value that cannot be
                // turned into a client throws here and costs no existing client its place.
                var created = CreateClient(base64Certificate);

                // Stamped before it is added, so a request that makes room at the same moment
                // does not take the new client for the one used longest ago.
                created.LastUsed = Interlocked.Increment(ref _useCounter);
                created.LastUsedAt = _timeProvider.GetTimestamp();
                MakeRoomForOneMore();

                cached = _clients.GetOrAdd(key, created);
                if (!ReferenceEquals(cached, created))
                {
                    // Another request added the same header value first. This one was never handed out.
                    created.Dispose();
                }
            }

            cached.LastUsed = Interlocked.Increment(ref _useCounter);
            cached.LastUsedAt = _timeProvider.GetTimestamp();
            return cached.Client;
        }

        private void DropUnusedClients()
        {
            foreach (var entry in _clients)
            {
                if (_timeProvider.GetElapsedTime(entry.Value.LastUsedAt) >= UnusedClientLifetime)
                {
                    // Dropped, not disposed, as when the cache is full: a request that got the
                    // client a moment ago must be able to finish. The certificate and its key
                    // are released when the garbage collector takes the client.
                    _clients.TryRemove(entry);
                }
            }
        }

        private void MakeRoomForOneMore()
        {
            while (_clients.Count >= MaxClients)
            {
                // A copy, because Dispose can empty the cache at any moment.
                var clients = _clients.ToArray();
                if (clients.Length == 0)
                {
                    return;
                }

                var usedLongestAgo = clients.MinBy(entry => entry.Value.LastUsed);

                // Dropped, not disposed: a request may still be using the client. Its idle
                // connections close after TargetHttpHandler.ConnectionIdleTimeout, and the rest
                // is left to the garbage collector.
                _clients.TryRemove(usedLongestAgo);
            }
        }

        private CachedClient CreateClient(string base64Certificate)
        {
            // The header holds the client certificate with its private key, and may also hold
            // the certificates of the authorities that issued it.
            var certificates = X509CertificateLoader.LoadPkcs12Collection(Convert.FromBase64String(base64Certificate), password: null);
            try
            {
                var certificate = certificates.FirstOrDefault(candidate => candidate.HasPrivateKey)
                    ?? throw new CryptographicException("The certificate header holds no certificate with a private key.");

                var handler = TargetHttpHandler.Create(_settings, _configureHandler);

                // The target is sent the certificate together with the issuer certificates from
                // the header. With offline set, nothing is fetched: the address to fetch from
                // stands in the certificate, so a caller could otherwise make the proxy call an
                // address of the caller's choice. Without a context, the handler would also
                // fetch from that address, in every handshake.
                handler.SslOptions.ClientCertificateContext =
                    SslStreamCertificateContext.Create(certificate, additionalCertificates: certificates, offline: true);

                // A TLS session set up with this certificate must not be resumed by another
                // connection. On Windows, a later connection without a certificate otherwise
                // resumes it, and the target sees this certificate on a request that did not
                // carry it. Linux never resumes sessions that have a client certificate.
                handler.SslOptions.AllowTlsResume = false;

                // The time limit is set per request by the proxy service.
                var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

                return new CachedClient(client);
            }
            catch
            {
                // No client was made, so nothing can be using the certificates.
                foreach (var loaded in certificates)
                {
                    loaded.Dispose();
                }

                throw;
            }
        }

        public void Dispose()
        {
            _unusedClientTimer.Dispose();

            foreach (var cached in _clients.Values)
            {
                cached.Dispose();
            }

            _clients.Clear();
        }

        private sealed class CachedClient : IDisposable
        {
            private long _lastUsed;
            private long _lastUsedAt;

            public CachedClient(HttpClient client)
            {
                Client = client;
            }

            public HttpClient Client { get; }

            // A counter rather than a clock, so the order is exact even for requests in the same millisecond.
            public long LastUsed
            {
                get => Interlocked.Read(ref _lastUsed);
                set => Interlocked.Exchange(ref _lastUsed, value);
            }

            // A timestamp from the provider's clock, for dropping clients that are no longer used.
            public long LastUsedAt
            {
                get => Interlocked.Read(ref _lastUsedAt);
                set => Interlocked.Exchange(ref _lastUsedAt, value);
            }

            // The certificates are not released here. A connection that is being set up when the
            // client is disposed goes on and still needs its certificate, and on Windows the
            // process dies if the certificate is gone. The handler holds on to them, and they
            // are released when nothing refers to the handler any more.
            public void Dispose() => Client.Dispose();
        }
    }
}
