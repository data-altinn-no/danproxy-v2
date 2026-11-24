using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dan.ProxyV2.Config;
using Dan.ProxyV2.UnitTest.TestSupport;
using OpenTelemetry;

namespace Dan.ProxyV2.UnitTest
{
    /// <summary>
    /// Runs the proxy service, wired up as in Program.cs, against a local HTTPS server and
    /// checks what the target receives and what the caller gets back.
    /// </summary>
    [TestClass]
    public class DanProxyServiceTests
    {
        private static readonly byte[] BinaryBody = [0x00, 0xFF, 0xFE, 0x80, 0x41, 0x1F, 0x8B];

        private static Dictionary<string, string?> Settings(params (string Name, string Value)[] settings) =>
            settings.ToDictionary(setting => setting.Name, setting => (string?)setting.Value);

        private static async Task<(HttpStatusCode Status, int ConnectionId, string Thumbprint)> CallAsync(ProxyFixture fixture, FakeHttpRequestData request)
        {
            var response = await fixture.SendAsync(request);
            var (connectionId, thumbprint) = MtlsTestServer.ParseResponseBody(response.BodyAsString());
            return (response.StatusCode, connectionId, thumbprint);
        }

        private static void RespondWith(MtlsTestServer server, int statusCode, byte[] body, params (string Name, string Value)[] headers) =>
            server.Respond = _ => Task.FromResult(new TestResponse(statusCode, headers, body));

        private static void RespondAfterFiveSeconds(MtlsTestServer server) =>
            server.Respond = async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), server.Stopping);
                return new TestResponse(200, [], []);
            };

        private static int ClosedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        // Validation of the target's certificate

        [TestMethod]
        public void Settings_HostListsAcceptEntriesWithPortSchemeOrPath()
        {
            var settings = new DanProxySettings
            {
                AllowedTargetHosts = "api.example.no:443, https://other.example.no/path, plain.example.no",
                IgnoreCertificateValidationHosts = "api.example.no:8443"
            };

            CollectionAssert.AreEqual(new[] { "api.example.no", "other.example.no", "plain.example.no" }, settings.AllowedTargetHostsList);
            CollectionAssert.AreEqual(new[] { "api.example.no" }, settings.IgnoreCertificateValidationHostsList);
        }

        [TestMethod]
        public void Settings_GiveATargetThirtySecondsAndAConnectionTenByDefault()
        {
            var settings = new DanProxySettings();

            Assert.AreEqual(30, settings.TargetTimeoutSeconds);
            Assert.AreEqual(10, settings.ConnectTimeoutSeconds);
        }

        [TestMethod]
        public void Settings_ValidateTargetCertificatesByDefault()
        {
            Assert.IsFalse(new DanProxySettings().IgnoreCertificateValidation);
        }

        [TestMethod]
        public async Task DefaultSettings_RejectTargetWithUntrustedCertificate()
        {
            await using var fixture = new ProxyFixture(trustTestServer: false);

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.AreEqual(0, fixture.Server.Requests.Count, "No request may reach a target whose certificate was not validated.");
        }

        [TestMethod]
        public async Task IgnoreCertificateValidationTurnedOn_AcceptsTargetWithUntrustedCertificate()
        {
            await using var fixture = new ProxyFixture(Settings(("IgnoreCertificateValidation", "true")), trustTestServer: false);

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async Task TargetInIgnoreValidationHosts_AcceptsUntrustedCertificateForThatHost()
        {
            await using var fixture = new ProxyFixture(Settings(("IgnoreCertificateValidationHosts", "127.0.0.1")), trustTestServer: false);

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async Task TargetWithoutValidation_ReusesOneConnection()
        {
            await using var fixture = new ProxyFixture(Settings(("IgnoreCertificateValidationHosts", "127.0.0.1")), trustTestServer: false);

            await fixture.SendAsync(fixture.CreateRequest());
            await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(2, fixture.Server.Requests.Count);
            Assert.AreEqual(1, fixture.Server.ConnectionCount);
        }

        // Client certificate from the request header

        [TestMethod]
        public async Task CertificateInHeader_IsPresentedToTargetAndConnectionIsReused()
        {
            await using var fixture = new ProxyFixture();
            var (certificate, thumbprint) = TestCertificates.CreateClientCertificate("client-a");

            var first = await CallAsync(fixture, fixture.CreateRequest(certificate: certificate));
            var second = await CallAsync(fixture, fixture.CreateRequest(certificate: certificate));

            Assert.AreEqual(HttpStatusCode.OK, first.Status);
            Assert.AreEqual(HttpStatusCode.OK, second.Status);
            Assert.AreEqual(thumbprint, first.Thumbprint);
            Assert.AreEqual(thumbprint, second.Thumbprint);
            Assert.AreEqual(first.ConnectionId, second.ConnectionId);
            Assert.AreEqual(1, fixture.Server.ConnectionCount);
        }

        [TestMethod]
        public async Task CertificateInHeader_TargetCertificateIsValidatedEvenWhenValidationIsTurnedOff()
        {
            // As in the first proxy. A call that proves who we are must know who it talks to.
            await using var fixture = new ProxyFixture(
                Settings(("IgnoreCertificateValidation", "true"), ("IgnoreCertificateValidationHosts", "127.0.0.1")), trustTestServer: false);
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");

            var response = await fixture.SendAsync(fixture.CreateRequest(certificate: certificate));

            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.AreEqual(0, fixture.Server.Requests.Count, "No request may reach a target whose certificate was not validated.");
        }

        [TestMethod]
        public async Task CertificateInHeader_IsNotForwardedToTarget()
        {
            await using var fixture = new ProxyFixture();
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            var request = fixture.CreateRequest(certificate: certificate);
            request.Headers.Add("Accept-Language", "nb");

            await fixture.SendAsync(request);

            var received = fixture.Server.Requests.Single();
            Assert.AreEqual("GET /some/path HTTP/1.1", received.RequestLine);
            Assert.AreEqual("nb", received.Headers["Accept-Language"], "Ordinary headers are forwarded.");
            Assert.IsFalse(received.Headers.ContainsKey(ProxyFixture.CertificateHeader), "The certificate and its private key must stay in the proxy.");
        }

        [TestMethod]
        public async Task RequestWithoutCertificate_DoesNotGetTheCertificateConnection()
        {
            await using var fixture = new ProxyFixture();
            var (certificate, thumbprint) = TestCertificates.CreateClientCertificate("client-a");

            var withCertificate = await CallAsync(fixture, fixture.CreateRequest(certificate: certificate));
            var withoutCertificate = await CallAsync(fixture, fixture.CreateRequest());

            Assert.AreEqual(thumbprint, withCertificate.Thumbprint);
            Assert.AreEqual(MtlsTestServer.NoCertificate, withoutCertificate.Thumbprint);
            Assert.AreNotEqual(withCertificate.ConnectionId, withoutCertificate.ConnectionId);
        }

        [TestMethod]
        public async Task CertificateHeaderWithANameThatDoesNotStartWithX_IsNotForwardedToTarget()
        {
            await using var fixture = new ProxyFixture(Settings(("CustomCertificateHeaderName", "Client-Certificate")));
            var (certificate, thumbprint) = TestCertificates.CreateClientCertificate("client-a");
            var request = fixture.CreateRequest();
            request.Headers.Add("Client-Certificate", certificate);

            await fixture.SendAsync(request);

            var received = fixture.Server.Requests.Single();
            Assert.AreEqual(thumbprint, received.ClientCertificateThumbprint, "The certificate from the header is used.");
            Assert.IsFalse(received.Headers.ContainsKey("Client-Certificate"), "The certificate and its private key must stay in the proxy, whatever the header is called.");
        }

        [TestMethod]
        public async Task CertificateHeaderThatIsNotACertificate_Gives400()
        {
            await using var fixture = new ProxyFixture();

            var response = await fixture.SendAsync(fixture.CreateRequest(certificate: "this is not base64"));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("invalid_certificate", response.Header(Constants.ProxyErrorHeader));
            Assert.AreEqual(0, fixture.Server.Requests.Count);
        }

        [TestMethod]
        public async Task CertificateHeaderWithoutPrivateKey_Gives400()
        {
            await using var fixture = new ProxyFixture();

            var response = await fixture.SendAsync(fixture.CreateRequest(certificate: TestCertificates.CreateCertificateWithoutPrivateKey()));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("invalid_certificate", response.Header(Constants.ProxyErrorHeader));
            Assert.AreEqual(0, fixture.Server.Requests.Count);
        }

        // The target url

        [TestMethod]
        public async Task Url_IsDecodedOnlyOnce()
        {
            await using var fixture = new ProxyFixture();

            await fixture.SendAsync(fixture.CreateRequest($"{fixture.Server.Authority}/some/path?time=10%3A00%2B01%3A00"));

            Assert.AreEqual("GET /some/path?time=10%3A00%2B01%3A00 HTTP/1.1", fixture.Server.Requests.Single().RequestLine);
        }

        [TestMethod]
        public async Task TargetHostNotInAllowedList_IsRejectedWithoutBeingCalled()
        {
            await using var fixture = new ProxyFixture(Settings(("AllowedTargetHosts", "api.example.no, other.example.no")));

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.AreEqual("host_not_allowed", response.Header(Constants.ProxyErrorHeader));
            Assert.AreEqual(0, fixture.Server.ConnectionCount);
        }

        [TestMethod]
        public async Task NoAllowedHostsConfigured_AllowsEveryTarget()
        {
            await using var fixture = new ProxyFixture(Settings(("AllowedTargetHosts", "")));

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(1, fixture.Server.Requests.Count);
        }

        [TestMethod]
        public async Task AllowedHostInFrontOfAtSign_DoesNotOpenForTheHostBehindIt()
        {
            await using var fixture = new ProxyFixture(Settings(("AllowedTargetHosts", "api.example.no")));

            var response = await fixture.SendAsync(fixture.CreateRequest($"api.example.no@{fixture.Server.Authority}/some/path"));

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.AreEqual(0, fixture.Server.ConnectionCount);
        }

        [TestMethod]
        public void Settings_HostListsAreReadInTheFormTheProxyConnectsTo()
        {
            // A name with letters outside ASCII is connected to in its encoded form, and an
            // IPv6 address without its brackets.
            var settings = new DanProxySettings
            {
                AllowedTargetHosts = "bærum.example.no, [::1]",
                IgnoreCertificateValidationHosts = "BÆRUM.example.no"
            };

            CollectionAssert.AreEqual(new[] { "xn--brum-voa.example.no", "::1" }, settings.AllowedTargetHostsList);
            CollectionAssert.AreEqual(new[] { "xn--brum-voa.example.no" }, settings.IgnoreCertificateValidationHostsList);
        }

        [TestMethod]
        public async Task AllowedHost_IsRecognisedWhicheverWayItsLettersOutsideAsciiAreWritten()
        {
            // The list has the encoded form of the name, and the url has the letters themselves.
            // The first connection attempt is made to fail, so no name is looked up.
            await using var fixture = new ProxyFixture(
                Settings(("AllowedTargetHosts", "xn--brum-voa.invalid"), ("ConnectRetries", "0")), failFirstConnects: 1);

            var response = await fixture.SendAsync(fixture.CreateRequest("bærum.invalid/some/path"));

            Assert.AreEqual("connection_failed", response.Header(Constants.ProxyErrorHeader));
            Assert.AreEqual(1, fixture.ConnectAttempts);
        }

        [TestMethod]
        public async Task AllowedIpv6Address_IsNotRefusedAsAHostOutsideTheList()
        {
            // Nothing listens on this address, so the call fails. It must fail as a connection
            // that could not be made, both where the url is checked and where the proxy connects.
            await using var fixture = new ProxyFixture(Settings(("AllowedTargetHosts", "[::1]"), ("ConnectRetries", "0")));

            var response = await fixture.SendAsync(fixture.CreateRequest($"[::1]:{fixture.Server.Port}/some/path"));

            Assert.AreEqual("connection_failed", response.Header(Constants.ProxyErrorHeader));
        }

        // What the target receives

        [TestMethod]
        public async Task RequestBody_ReachesTheTargetByteForByteWithItsContentType()
        {
            await using var fixture = new ProxyFixture();
            var request = fixture.CreateRequest(method: "POST", body: BinaryBody);
            request.Headers.Add("Content-Type", "application/octet-stream");

            await fixture.SendAsync(request);

            var received = fixture.Server.Requests.Single();
            Assert.AreEqual("POST /some/path HTTP/1.1", received.RequestLine);
            CollectionAssert.AreEqual(BinaryBody, received.Body);
            Assert.AreEqual("application/octet-stream", received.Headers["Content-Type"]);
        }

        [TestMethod]
        public async Task InternalHeaders_AreNotForwardedToTarget()
        {
            await using var fixture = new ProxyFixture();
            var request = fixture.CreateRequest();
            request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
            request.Headers.Add("tracestate", "vendor=value");
            request.Headers.Add("Forwarded", "for=10.0.0.4");
            request.Headers.Add("Max-Forwards", "10");
            request.Headers.Add("DISGUISED-HOST", "func-proxy.azurewebsites.net");
            request.Headers.Add("x-functions-key", "function-key");
            request.Headers.Add("Authorization", "Bearer token-for-the-target");
            request.Headers.Add("Accept-Language", "nb");

            await fixture.SendAsync(request);

            var received = fixture.Server.Requests.Single().Headers;
            foreach (var name in new[] { "traceparent", "tracestate", "Forwarded", "Max-Forwards", "DISGUISED-HOST", "x-functions-key" })
            {
                Assert.IsFalse(received.ContainsKey(name), $"{name} must not be forwarded.");
            }

            Assert.AreEqual("Bearer token-for-the-target", received["Authorization"]);
            Assert.AreEqual("nb", received["Accept-Language"]);
        }

        [TestMethod]
        public async Task TraceContextOfTheProxy_IsNotSentToTargetWhenCallsAreTracedAsInAzure()
        {
            // The tracing puts trace headers on outgoing calls by its own means, past the handler.
            await using var fixture = new ProxyFixture(traceCalls: true);
            using var activity = new Activity("incoming request").Start();
            Baggage.SetBaggage("caller", "a-plugin");

            try
            {
                await fixture.SendAsync(fixture.CreateRequest());
            }
            finally
            {
                Baggage.ClearBaggage();
            }

            var received = fixture.Server.Requests.Single().Headers;
            Assert.IsFalse(received.ContainsKey("traceparent"), "The proxy's own trace id must not be sent to the target.");
            Assert.IsFalse(received.ContainsKey("baggage"), "Baggage of the proxy's trace must not be sent to the target.");
        }

        [TestMethod]
        public async Task HeadersNamedInConnection_AreNotForwardedToTarget()
        {
            // Connection names the headers that only apply to the connection to the proxy.
            await using var fixture = new ProxyFixture();
            var request = fixture.CreateRequest();
            request.Headers.Add("Connection", "keep-alive, Session-Token");
            request.Headers.Add("Session-Token", "only-for-the-proxy");
            request.Headers.Add("Accept-Language", "nb");

            await fixture.SendAsync(request);

            var received = fixture.Server.Requests.Single().Headers;
            Assert.IsFalse(received.ContainsKey("Session-Token"), "A header named in Connection must not be forwarded.");
            Assert.AreEqual("nb", received["Accept-Language"]);
        }

        [TestMethod]
        public async Task TraceContextOfTheProxy_IsNotSentToTarget()
        {
            await using var fixture = new ProxyFixture();
            using var activity = new Activity("incoming request").Start();

            await fixture.SendAsync(fixture.CreateRequest());

            var received = fixture.Server.Requests.Single().Headers;
            Assert.IsFalse(received.ContainsKey("traceparent"), "The proxy's own trace id must not be sent to the target.");
            Assert.IsFalse(received.ContainsKey("Request-Id"));
        }

        [TestMethod]
        public async Task IgnoredHeaders_AreMatchedWithoutRegardToCase()
        {
            await using var fixture = new ProxyFixture(Settings(("Ignored", "accept-language")));
            var request = fixture.CreateRequest();
            request.Headers.Add("Accept-Language", "nb");

            await fixture.SendAsync(request);

            Assert.IsFalse(fixture.Server.Requests.Single().Headers.ContainsKey("Accept-Language"));
        }

        [TestMethod]
        public async Task Cookies_AreNotCarriedFromOneRequestToTheNext()
        {
            await using var fixture = new ProxyFixture();

            // The test server sets a cookie on every response.
            await fixture.SendAsync(fixture.CreateRequest());
            await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(2, fixture.Server.Requests.Count);
            foreach (var received in fixture.Server.Requests)
            {
                Assert.IsFalse(received.Headers.ContainsKey("Cookie"), "The shared handler must not replay cookies.");
            }
        }

        [TestMethod]
        public async Task CallToTarget_IsReportedAsAnActivity()
        {
            // Application Insights only gets a dependency for a call that HttpClient reports as an activity.
            var started = new ConcurrentQueue<string>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "System.Net.Http",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity => started.Enqueue(activity.OperationName)
            };
            ActivitySource.AddActivityListener(listener);
            await using var fixture = new ProxyFixture();

            await fixture.SendAsync(fixture.CreateRequest());

            Assert.IsTrue(started.Contains("System.Net.Http.HttpRequestOut"), "Without the activity, calls to targets are missing from the telemetry.");
            Assert.IsFalse(fixture.Server.Requests.Single().Headers.ContainsKey("traceparent"), "The trace id must still not be sent to the target.");
        }

        // What the caller gets back

        [TestMethod]
        public async Task Response_KeepsBodyAndTheHeadersACallerNeeds()
        {
            await using var fixture = new ProxyFixture();
            RespondWith(fixture.Server, 429, BinaryBody,
                ("Content-Type", "application/octet-stream"),
                ("Content-Encoding", "gzip"),
                ("Retry-After", "120"),
                ("WWW-Authenticate", "Bearer realm=\"target\""),
                ("Set-Cookie", "session=abc"),
                ("Server", "target-server"));

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
            CollectionAssert.AreEqual(BinaryBody, response.BodyAsBytes());
            Assert.AreEqual("application/octet-stream", response.Header("Content-Type"));
            Assert.AreEqual("gzip", response.Header("Content-Encoding"));
            Assert.AreEqual("120", response.Header("Retry-After"));
            Assert.AreEqual("Bearer realm=\"target\"", response.Header("WWW-Authenticate"));
            Assert.IsNull(response.Header("Set-Cookie"));
            Assert.IsNull(response.Header("Server"));
            Assert.IsNull(response.Header(Constants.ProxyErrorHeader), "Only errors made by the proxy itself carry the error header.");
        }

        [TestMethod]
        public async Task Redirect_IsFollowedByTheProxy()
        {
            await using var fixture = new ProxyFixture();
            fixture.Server.Respond = request => Task.FromResult(request.RequestLine.StartsWith("GET /some/path ", StringComparison.Ordinal)
                ? new TestResponse(302, [("Location", "/somewhere/else")], [])
                : new TestResponse(200, [("Content-Type", "text/plain")], "moved here"u8.ToArray()));

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("moved here", response.BodyAsString());
            Assert.AreEqual("GET /somewhere/else HTTP/1.1", fixture.Server.Requests.Last().RequestLine);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task RedirectToAHostThatIsNotAllowed_IsNotFollowed(bool withCertificate)
        {
            // "localhost" is the same test server, but it is not in the list, which only has 127.0.0.1.
            await using var fixture = new ProxyFixture();
            RespondWith(fixture.Server, 302, [], ("Location", $"https://localhost:{fixture.Server.Port}/somewhere/else"));
            var certificate = withCertificate ? TestCertificates.CreateClientCertificate("client-a").Base64Pfx : null;

            var response = await fixture.SendAsync(fixture.CreateRequest(certificate: certificate));

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.AreEqual("host_not_allowed", response.Header(Constants.ProxyErrorHeader));
            Assert.AreEqual(1, fixture.Server.Requests.Count, "A target must not be able to send the proxy on to a host outside the list.");
        }

        [TestMethod]
        public async Task RedirectThatIsNotFollowed_IsReturnedWithoutALocationHeader()
        {
            // A redirect from https to http is never followed.
            await using var fixture = new ProxyFixture();
            var location = $"http://{fixture.Server.Authority}/somewhere/else";
            RespondWith(fixture.Server, 302, [], ("Location", location));

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
            Assert.AreEqual(location, response.Header(Constants.TargetLocationHeader));
            Assert.IsNull(response.Header("Location"),
                "A caller that follows redirects would send its certificate header, with the private key, to the address in Location.");
            Assert.AreEqual(1, fixture.Server.Requests.Count);
        }

        // Failures

        [TestMethod]
        public async Task TargetThatCannotBeReached_Gives502()
        {
            await using var fixture = new ProxyFixture();

            var response = await fixture.SendAsync(fixture.CreateRequest($"127.0.0.1:{ClosedPort()}/some/path"));

            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.AreEqual("connection_failed", response.Header(Constants.ProxyErrorHeader));
        }

        [TestMethod]
        public async Task ConnectionThatFailsOnce_IsTriedAgainAndTheCallSucceeds()
        {
            await using var fixture = new ProxyFixture(failFirstConnects: 1);

            var response = await fixture.SendAsync(fixture.CreateRequest(method: "POST", body: BinaryBody));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(2, fixture.ConnectAttempts);
            CollectionAssert.AreEqual(BinaryBody, fixture.Server.Requests.Single().Body, "The second attempt must carry the whole body.");
        }

        [TestMethod]
        public async Task ConnectionThatKeepsFailing_Gives502AfterOneNewAttempt()
        {
            await using var fixture = new ProxyFixture(failFirstConnects: 10);

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.AreEqual("connection_failed", response.Header(Constants.ProxyErrorHeader));
            Assert.AreEqual(2, fixture.ConnectAttempts);
        }

        [TestMethod]
        public async Task ConnectionThatHangs_IsGivenUpAndTriedAgain()
        {
            await using var fixture = new ProxyFixture(Settings(("ConnectTimeoutSeconds", "1")), hangFirstConnects: 1);
            var stopwatch = Stopwatch.StartNew();

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(2, fixture.ConnectAttempts);
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
        }

        [TestMethod]
        public async Task ConnectionThatKeepsHanging_Gives502AfterOneNewAttempt()
        {
            await using var fixture = new ProxyFixture(Settings(("ConnectTimeoutSeconds", "1")), hangFirstConnects: 10);

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.AreEqual("connection_failed", response.Header(Constants.ProxyErrorHeader));
            Assert.AreEqual(2, fixture.ConnectAttempts);
        }

        [TestMethod]
        public async Task ConnectionFailureAfterARedirect_DoesNotSendTheFirstRequestAgain()
        {
            // The target has answered the request, with a redirect to an address nobody listens on.
            await using var fixture = new ProxyFixture();
            RespondWith(fixture.Server, 303, [], ("Location", $"https://127.0.0.1:{ClosedPort()}/somewhere/else"));

            var response = await fixture.SendAsync(fixture.CreateRequest(method: "POST", body: BinaryBody));

            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.AreEqual(1, fixture.Server.Requests.Count, "The target may have acted on the request before it redirected, so it must not get it twice.");
        }

        [TestMethod]
        public async Task ConnectRetriesSetToZero_DoesNotTryAgain()
        {
            await using var fixture = new ProxyFixture(Settings(("ConnectRetries", "0")), failFirstConnects: 1);

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.AreEqual(1, fixture.ConnectAttempts);
        }

        [TestMethod]
        public async Task TargetThatDropsTheConnectionAfterTheRequest_IsNotCalledAgain()
        {
            await using var fixture = new ProxyFixture();
            fixture.Server.Respond = _ => throw new IOException("Close the connection without answering.");

            var response = await fixture.SendAsync(fixture.CreateRequest(method: "POST", body: BinaryBody));

            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.AreEqual("connection_failed", response.Header(Constants.ProxyErrorHeader));
            Assert.AreEqual(1, fixture.Server.Requests.Count, "The target may have acted on the request, so it must not be sent twice.");
        }

        [TestMethod]
        public async Task TargetSlowerThanTheTimeout_Gives504()
        {
            await using var fixture = new ProxyFixture(Settings(("TargetTimeoutSeconds", "1")));
            RespondAfterFiveSeconds(fixture.Server);
            var stopwatch = Stopwatch.StartNew();

            var response = await fixture.SendAsync(fixture.CreateRequest());

            Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.AreEqual("timeout", response.Header(Constants.ProxyErrorHeader));
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Gave up after {stopwatch.Elapsed}.");
        }

        [TestMethod]
        public async Task CallerThatGivesUp_StopsTheCallToTarget()
        {
            await using var fixture = new ProxyFixture();
            var server = fixture.Server;
            var targetHasTheRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Respond = async _ =>
            {
                targetHasTheRequest.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(5), server.Stopping);
                return new TestResponse(200, [], []);
            };
            using var callerGaveUp = new CancellationTokenSource();

            // Given up only when the target has the request, so it is the call to the target that is stopped.
            var call = fixture.SendAsync(fixture.CreateRequest(), callerGaveUp.Token);
            await targetHasTheRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stopwatch = Stopwatch.StartNew();
            callerGaveUp.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => call);

            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Kept going for {stopwatch.Elapsed}.");
        }

        // Logging

        [TestMethod]
        public async Task DebugMode_LogsNoValuesOfHeadersItDoesNotKnowToBeHarmless()
        {
            // The certificate header is not configured, so the proxy cannot know that the header
            // below carries a private key. An API key in a header of the caller's own is just as unknown.
            await using var fixture = new ProxyFixture(Settings(("DebugMode", "true"), ("CustomCertificateHeaderName", "")));
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            var request = fixture.CreateRequest(certificate: certificate);
            request.Headers.Add("Api-Key", "key-for-the-target");

            await fixture.SendAsync(request);

            var log = string.Join(Environment.NewLine, fixture.Logs.Messages);
            Assert.IsTrue(log.Contains("Api-Key"), "Header names are logged in debug mode.");
            Assert.IsFalse(log.Contains(certificate), "A header that may hold a private key must never be logged.");
            Assert.IsFalse(log.Contains("key-for-the-target"), "A header that may hold a credential must never be logged.");
        }

        [TestMethod]
        public async Task DebugMode_LogsHeaderNamesButNotSecrets()
        {
            await using var fixture = new ProxyFixture(Settings(("DebugMode", "true")));
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            var request = fixture.CreateRequest(certificate: certificate);
            request.Headers.Add("Authorization", "Bearer token-for-the-target");
            request.Headers.Add("Accept-Language", "nb");

            await fixture.SendAsync(request);

            var log = string.Join(Environment.NewLine, fixture.Logs.Messages);
            Assert.IsTrue(log.Contains("Authorization"), "Header names are logged in debug mode.");
            Assert.IsTrue(log.Contains("nb"), "Ordinary header values are logged in debug mode.");
            Assert.IsFalse(log.Contains(certificate), "The certificate with its private key must never be logged.");
            Assert.IsFalse(log.Contains("token-for-the-target"), "Credentials for the target must never be logged.");
        }

        [TestMethod]
        public async Task DebugMode_NeverLogsTheCertificateHeaderWhateverItIsCalled()
        {
            // User-Agent is among the headers whose value is otherwise logged.
            await using var fixture = new ProxyFixture(Settings(("DebugMode", "true"), ("CustomCertificateHeaderName", "User-Agent")));
            var (certificate, _) = TestCertificates.CreateClientCertificate("client-a");
            var request = fixture.CreateRequest();
            request.Headers.TryAddWithoutValidation("User-Agent", certificate);

            await fixture.SendAsync(request);

            var log = string.Join(Environment.NewLine, fixture.Logs.Messages);
            Assert.IsTrue(log.Contains("User-Agent"), "Header names are logged in debug mode.");
            Assert.IsFalse(log.Contains(certificate), "The certificate with its private key must never be logged.");
        }
    }
}
