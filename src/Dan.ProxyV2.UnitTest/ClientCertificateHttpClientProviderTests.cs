using System.Net.Sockets;
using Dan.ProxyV2.Config;
using Dan.ProxyV2.Interfaces;
using Dan.ProxyV2.Services;
using Dan.ProxyV2.UnitTest.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Dan.ProxyV2.UnitTest
{
    [TestClass]
    public class ClientCertificateHttpClientProviderTests
    {
        [TestMethod]
        public void GetClient_SameCertificate_ReturnsSameClient()
        {
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            using var provider = new ClientCertificateHttpClientProvider();

            var first = provider.GetClient(certificate);
            var second = provider.GetClient(certificate);

            Assert.AreSame(first, second);
        }

        [TestMethod]
        public void GetClient_DifferentCertificates_ReturnsDifferentClients()
        {
            var (certificateA, _) = TestCertificates.CreateClientCertificate("client-a");
            var (certificateB, _) = TestCertificates.CreateClientCertificate("client-b");
            using var provider = new ClientCertificateHttpClientProvider();

            var clientA = provider.GetClient(certificateA);
            var clientB = provider.GetClient(certificateB);

            Assert.AreNotSame(clientA, clientB);
        }

        [TestMethod]
        public async Task GetClient_ConcurrentFirstCalls_CreateOneClient()
        {
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            using var provider = new ClientCertificateHttpClientProvider();

            var clients = await Task.WhenAll(
                Enumerable.Range(0, 50).Select(_ => Task.Run(() => provider.GetClient(certificate))));

            Assert.AreEqual(1, clients.Distinct().Count());
        }

        [TestMethod]
        public void GetClient_InvalidHeaderValue_ThrowsEveryTimeAndDoesNotBlockValidCertificates()
        {
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            using var provider = new ClientCertificateHttpClientProvider();

            Assert.ThrowsExactly<FormatException>(() => provider.GetClient("this is not base64"));
            Assert.ThrowsExactly<FormatException>(() => provider.GetClient("this is not base64"));

            Assert.IsNotNull(provider.GetClient(certificate));
        }

        [TestMethod]
        public void DistinctHeaderValues_AreDifferentStringsForTheSameCertificate()
        {
            // The premise for the limit tests below, and the reason the limit exists.
            var headerValues = TestCertificates.CreateDistinctHeaderValues(3);

            Assert.AreEqual(3, headerValues.Distinct().Count());
        }

        [TestMethod]
        public void GetClient_MoreHeaderValuesThanTheLimit_KeepsTheCacheAtTheLimit()
        {
            var headerValues = TestCertificates.CreateDistinctHeaderValues(ClientCertificateHttpClientProvider.MaxClients + 5);
            using var provider = new ClientCertificateHttpClientProvider();

            foreach (var headerValue in headerValues)
            {
                provider.GetClient(headerValue);
            }

            Assert.AreEqual(ClientCertificateHttpClientProvider.MaxClients, provider.CachedClientCount);
        }

        [TestMethod]
        public void GetClient_WhenTheCacheIsFull_DropsTheClientUsedLongestAgo()
        {
            var limit = ClientCertificateHttpClientProvider.MaxClients;
            var headerValues = TestCertificates.CreateDistinctHeaderValues(limit + 1);
            using var provider = new ClientCertificateHttpClientProvider();
            var clients = headerValues.Take(limit).Select(provider.GetClient).ToArray();

            // Using the oldest client again leaves the second one as the one used longest ago.
            provider.GetClient(headerValues[0]);
            provider.GetClient(headerValues[limit]);

            Assert.AreSame(clients[0], provider.GetClient(headerValues[0]), "A client in use must survive.");
            Assert.AreNotSame(clients[1], provider.GetClient(headerValues[1]), "The client used longest ago must have been dropped.");
        }

        [TestMethod]
        public void GetClient_InvalidHeaderValueWhenTheCacheIsFull_DropsNothing()
        {
            var limit = ClientCertificateHttpClientProvider.MaxClients;
            var headerValues = TestCertificates.CreateDistinctHeaderValues(limit);
            using var provider = new ClientCertificateHttpClientProvider();
            var clients = headerValues.Select(provider.GetClient).ToArray();

            Assert.ThrowsExactly<FormatException>(() => provider.GetClient("this is not base64"));

            Assert.AreEqual(limit, provider.CachedClientCount);
            Assert.AreSame(clients[0], provider.GetClient(headerValues[0]));
        }

        [TestMethod]
        public void ClientThatIsNotUsedForAnHour_IsDropped()
        {
            // Otherwise the private key of a certificate that has been replaced stays in memory
            // for as long as the process lives.
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            var time = new FakeTimeProvider();
            using var provider = new ClientCertificateHttpClientProvider(new DanProxySettings(), null, time);
            provider.GetClient(certificate);

            time.Advance(ClientCertificateHttpClientProvider.UnusedClientLifetime + ClientCertificateHttpClientProvider.UnusedClientCheckInterval);

            Assert.AreEqual(0, provider.CachedClientCount);
        }

        [TestMethod]
        public void ClientThatIsUsedWithinTheHour_IsKept()
        {
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            var time = new FakeTimeProvider();
            using var provider = new ClientCertificateHttpClientProvider(new DanProxySettings(), null, time);
            var client = provider.GetClient(certificate);

            time.Advance(TimeSpan.FromMinutes(40));
            provider.GetClient(certificate);
            time.Advance(TimeSpan.FromMinutes(40));

            Assert.AreSame(client, provider.GetClient(certificate));
        }

        [TestMethod]
        public void ClientThatIsDroppedAsUnused_IsNotDisposed()
        {
            // A request that got the client just before it was dropped must be able to finish.
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            var time = new FakeTimeProvider();
            using var provider = new ClientCertificateHttpClientProvider(new DanProxySettings(), null, time);
            var client = provider.GetClient(certificate);

            time.Advance(ClientCertificateHttpClientProvider.UnusedClientLifetime + ClientCertificateHttpClientProvider.UnusedClientCheckInterval);

            Assert.AreEqual(0, provider.CachedClientCount);
            client.CancelPendingRequests(); // Throws if the client has been disposed.
        }

        [TestMethod]
        public async Task Dispose_DisposesTheClientsItCreated()
        {
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            var provider = new ClientCertificateHttpClientProvider();
            var client = provider.GetClient(certificate);

            provider.Dispose();

            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => client.GetAsync("https://127.0.0.1:1/"));
        }

        [TestMethod]
        public async Task Dispose_WhileAConnectionIsBeingSetUp_LeavesItsCertificateUsable()
        {
            // A connection attempt goes on after the request that started it is gone. If its
            // certificate has been released by then, the process dies on Windows.
            await using var target = new MtlsTestServer();
            var (certificate, thumbprint) = TestCertificates.CreateClientCertificate("client-a");
            var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mayConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new ClientCertificateHttpClientProvider(handler =>
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, serverCertificate, _, _) => target.IsServerCertificate(serverCertificate);
                handler.ConnectCallback = async (context, _) =>
                {
                    connecting.TrySetResult();
                    await mayConnect.Task;

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await socket.ConnectAsync(context.DnsEndPoint);
                    return new NetworkStream(socket, ownsSocket: true);
                };
            });

            var call = provider.GetClient(certificate).GetAsync(target.BaseAddress);
            await connecting.Task;
            provider.Dispose();
            mayConnect.SetResult();

            await Assert.ThrowsAsync<OperationCanceledException>(() => call);
            for (var waited = 0; target.Handshakes.Count == 0 && waited < 5000; waited += 50)
            {
                await Task.Delay(50);
            }

            Assert.AreEqual(thumbprint, target.Handshakes.Single());
        }

        [TestMethod]
        public void RegisteredAsInProgram_ResolvesAsOneSharedInstance()
        {
            var services = new ServiceCollection();
            services.AddDanProxy(new ConfigurationBuilder().Build());
            using var serviceProvider = services.BuildServiceProvider();

            var first = serviceProvider.GetRequiredService<IClientCertificateHttpClientProvider>();
            var second = serviceProvider.GetRequiredService<IClientCertificateHttpClientProvider>();

            Assert.AreSame(first, second);
        }
    }
}
