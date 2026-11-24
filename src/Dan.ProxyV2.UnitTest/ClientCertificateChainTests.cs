using Dan.ProxyV2.Services;
using Dan.ProxyV2.UnitTest.TestSupport;

namespace Dan.ProxyV2.UnitTest
{
    /// <summary>
    /// A target is sent the client certificate together with the certificates of the
    /// authorities that issued it. The caller puts them in the certificate header. The proxy
    /// never fetches them itself: the address to fetch from stands in the certificate, so a
    /// caller could use it to make the proxy call an address of the caller's choice.
    /// </summary>
    [TestClass]
    public class ClientCertificateChainTests
    {
        /// <summary>The target closes the connection after each response, so every call makes a new handshake.</summary>
        private static MtlsTestServer CreateTargetWithoutKeepAlive()
        {
            var target = new MtlsTestServer();
            target.Respond = _ => Task.FromResult(new TestResponse(200, [("Connection", "close")], []));
            return target;
        }

        private static async Task CallAsync(HttpClient client, MtlsTestServer target)
        {
            using var response = await client.GetAsync(target.BaseAddress);
            response.EnsureSuccessStatusCode();
        }

        private static ClientCertificateHttpClientProvider CreateProvider(MtlsTestServer target) =>
            new(handler => handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, _, _) => target.IsServerCertificate(certificate));

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public async Task ChainInTheHeader_IsSentToTheTarget()
        {
            // The download site is down, so the intermediate certificate can only come from the header.
            await using var downloads = new CertificateDownloadServer { Available = false };
            await using var target = CreateTargetWithoutKeepAlive();
            var certificate = TestCertificates.CreateClientCertificateWithChain(downloads);
            using var provider = CreateProvider(target);

            await CallAsync(provider.GetClient(certificate.PfxWithChain), target);

            Assert.AreEqual(certificate.Thumbprint, target.Requests.Single().ClientCertificateThumbprint);
            Assert.IsTrue(target.WasSentByClient(certificate.IntermediateThumbprint), "The target must get the intermediate certificate from the header.");
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public async Task DownloadSiteNamedInTheCertificate_IsNeverCalled()
        {
            await using var downloads = new CertificateDownloadServer();
            await using var target = CreateTargetWithoutKeepAlive();
            var certificate = TestCertificates.CreateClientCertificateWithChain(downloads);
            using var provider = CreateProvider(target);

            var client = provider.GetClient(certificate.PfxWithoutChain);
            await CallAsync(client, target);
            await CallAsync(client, target);

            Assert.AreEqual(2, target.ConnectionCount, "The two calls must have made a handshake each.");
            Assert.AreEqual(0, downloads.Requests, "The proxy must not call an address taken from a certificate the caller sent.");
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public async Task StatusSiteNamedInTheCertificate_IsNeverCalled()
        {
            // With the issuer at hand, the revocation status of the certificate could be asked
            // for. The address for that also stands in the certificate.
            await using var downloads = new CertificateDownloadServer();
            await using var target = CreateTargetWithoutKeepAlive();
            var certificate = TestCertificates.CreateClientCertificateWithChain(downloads);
            using var provider = CreateProvider(target);

            var client = provider.GetClient(certificate.PfxWithChain);
            await CallAsync(client, target);
            await CallAsync(client, target);
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.AreEqual(string.Empty, string.Join(", ", downloads.RequestedPaths), "The proxy must not call an address taken from a certificate the caller sent.");
        }

        [TestMethod]
        [OSCondition(OperatingSystems.Linux)]
        public async Task CertificateWithoutItsChain_IsStillPresentedToTheTarget()
        {
            await using var downloads = new CertificateDownloadServer();
            await using var target = CreateTargetWithoutKeepAlive();
            var certificate = TestCertificates.CreateClientCertificateWithChain(downloads);
            using var provider = CreateProvider(target);

            await CallAsync(provider.GetClient(certificate.PfxWithoutChain), target);

            Assert.AreEqual(certificate.Thumbprint, target.Requests.Single().ClientCertificateThumbprint);
            Assert.IsFalse(target.WasSentByClient(certificate.IntermediateThumbprint));
        }
    }
}
