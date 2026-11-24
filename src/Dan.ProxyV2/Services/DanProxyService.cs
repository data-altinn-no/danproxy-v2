using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dan.ProxyV2.Config;
using Dan.ProxyV2.Interfaces;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dan.ProxyV2.Services
{
    public class DanProxyService : IDanProxyService
    {
        private const string DefaultRequestContentType = "text/plain; charset=utf-8";
        private const string DefaultResponseContentType = "application/json";

        // Long enough to get past a momentary failure, short enough that a caller hardly notices it.
        private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(250);

        private readonly ILogger<DanProxyService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IClientCertificateHttpClientProvider _clientCertificateHttpClientProvider;
        private readonly DanProxySettings _settings;

        // The lists in the settings are parsed each time they are read, so they are read once here.
        private readonly string[] _allowedHosts;
        private readonly string[] _ignoreValidationHosts;
        private readonly string[] _headersNotForwarded;

        public DanProxyService(
            ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory,
            IClientCertificateHttpClientProvider clientCertificateHttpClientProvider, IOptions<DanProxySettings> settings)
        {
            _logger = loggerFactory.CreateLogger<DanProxyService>();
            _httpClientFactory = httpClientFactory;
            _clientCertificateHttpClientProvider = clientCertificateHttpClientProvider;
            _settings = settings.Value;

            _allowedHosts = _settings.AllowedTargetHostsList;
            _ignoreValidationHosts = _settings.IgnoreCertificateValidationHostsList;

            // The certificate header holds a private key and stays in the proxy, whatever it is called.
            _headersNotForwarded = string.IsNullOrEmpty(_settings.CustomCertificateHeaderName)
                ? _settings.IgnoredHeaders
                : [.. _settings.IgnoredHeaders, _settings.CustomCertificateHeaderName];
        }

        public async Task<HttpResponseData> ProxyRequest(HttpRequestData incomingRequest, CancellationToken cancellationToken = default)
        {
            if (_settings.DebugMode)
            {
                _logger.LogInformation(
                    "Debug mode enabled - IgnoreCertificateValidation: {IgnoreCertificateValidation}, CustomCertificateHeaderName: {CustomCertificateHeaderName}, IgnoredHeaders: {IgnoredHeaders}",
                    _settings.IgnoreCertificateValidation, _settings.CustomCertificateHeaderName, string.Join(",", _settings.IgnoredHeaders));

                foreach (var header in incomingRequest.Headers)
                {
                    LogHeader("Incoming", header.Key, header.Value);
                }
            }

            // The Functions worker has already decoded the query string. Decoding it once more
            // would change an encoded "+" or "%" in the target's own query string.
            var url = "https://" + incomingRequest.Query["url"];

            if (!Uri.TryCreate(url, UriKind.Absolute, out var targetUri) || string.IsNullOrEmpty(targetUri.Host))
            {
                return await ErrorAsync(incomingRequest, HttpStatusCode.BadRequest, "invalid_url", "Invalid url provided");
            }

            // In the same form as the entries of the host lists.
            var targetHost = DanProxySettings.HostName(targetUri);

            // An empty list allows every host.
            if (_allowedHosts.Length > 0 && !_allowedHosts.Contains(targetHost, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Refused proxy request to host {TargetHost}, which is not in AllowedTargetHosts", targetHost);
                return await ErrorAsync(incomingRequest, HttpStatusCode.Forbidden, "host_not_allowed", "The target host is not allowed");
            }

            _logger.LogInformation("Proxy request to host {TargetHost}", targetHost);

            var client = SelectClient(incomingRequest, targetHost);
            if (client is null)
            {
                return await ErrorAsync(incomingRequest, HttpStatusCode.BadRequest, "invalid_certificate", "Invalid client certificate header");
            }

            var method = HttpMethod.Parse(incomingRequest.Method);
            byte[]? body = null;

            if (method != HttpMethod.Get)
            {
                // Sent on as the bytes that came in, so binary content and other character sets survive.
                using var buffer = new MemoryStream();
                await incomingRequest.Body.CopyToAsync(buffer, cancellationToken);

                if (buffer.Length > 0)
                {
                    body = buffer.ToArray();

                    if (_settings.DebugMode)
                    {
                        _logger.LogInformation("Incoming::: body {Body}", Encoding.UTF8.GetString(body));
                    }
                }
            }

            // Covers every attempt: the wait for the response and the reading of its body.
            using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_settings.TargetTimeoutSeconds > 0)
            {
                timeLimit.CancelAfter(TimeSpan.FromSeconds(_settings.TargetTimeoutSeconds));
            }

            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    // A request message can only be sent once, so each attempt gets its own.
                    using var outgoingRequest = new HttpRequestMessage(method, targetUri);
                    if (body is not null)
                    {
                        outgoingRequest.Content = new ByteArrayContent(body);
                    }

                    CopyRequestHeaders(incomingRequest, outgoingRequest);

                    try
                    {
                        using var targetResponse = await client.SendAsync(outgoingRequest, HttpCompletionOption.ResponseHeadersRead, timeLimit.Token);

                        _logger.LogInformation("Response from host {TargetHost} is {StatusCode}", targetHost, targetResponse.StatusCode);

                        var outgoingResponse = incomingRequest.CreateResponse(targetResponse.StatusCode);
                        CopyResponseHeaders(targetResponse, outgoingResponse);
                        await targetResponse.Content.CopyToAsync(outgoingResponse.Body, timeLimit.Token);

                        return outgoingResponse;
                    }
                    catch (Exception ex) when (attempt <= _settings.ConnectRetries
                        && FailedToConnect(ex, timeLimit.Token)
                        // The handler puts a new address on the request when it follows a redirect.
                        // The target has then answered the request once, and must not get it again.
                        && ReferenceEquals(outgoingRequest.RequestUri, targetUri))
                    {
                        // Logged with the exception, so failed connections can still be counted when the next attempt succeeds.
                        _logger.LogWarning(ex, "Could not connect to host {TargetHost} on attempt {Attempt}, trying again", targetHost, attempt);
                    }

                    await Task.Delay(ConnectRetryDelay, timeLimit.Token);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller has given up, so there is nobody to answer.
                _logger.LogInformation("Proxy request to host {TargetHost} was cancelled by the caller", targetHost);
                throw;
            }
            catch (OperationCanceledException ex) when (timeLimit.IsCancellationRequested)
            {
                _logger.LogError(ex, "No complete response from host {TargetHost} within {TimeoutSeconds} seconds", targetHost, _settings.TargetTimeoutSeconds);
                return await ErrorAsync(incomingRequest, HttpStatusCode.GatewayTimeout, "timeout", "The target did not respond in time");
            }
            catch (HttpRequestException ex) when (ex.InnerException is TargetHostNotAllowedException notAllowed)
            {
                // The host the caller asked for was checked above, so this is a redirect from the target.
                _logger.LogWarning("Host {TargetHost} redirected to host {RedirectHost}, which is not in AllowedTargetHosts", targetHost, notAllowed.Host);
                return await ErrorAsync(incomingRequest, HttpStatusCode.Forbidden, "host_not_allowed", "The target redirected to a host that is not allowed");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // A cancellation here is a connection that was not set up within ConnectTimeoutSeconds.
                _logger.LogError(ex, "Error proxying request to host {TargetHost}", targetHost);
                return await ErrorAsync(incomingRequest, HttpStatusCode.BadGateway, "connection_failed", "The target could not be reached");
            }
        }

        /// <summary>
        /// True when no connection to the target was made: the name lookup or the connect
        /// failed, or there was no connection within ConnectTimeoutSeconds. The target has then
        /// not received any part of the request, so sending it again cannot make it act twice.
        /// </summary>
        private static bool FailedToConnect(Exception exception, CancellationToken timeLimit) => exception switch
        {
            // Trying again would only be refused again.
            HttpRequestException { InnerException: TargetHostNotAllowedException } => false,
            HttpRequestException failure => failure.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError,
            // The handler gave the connection attempt up. When the whole time limit has passed, there is no time to try again.
            OperationCanceledException { InnerException: TimeoutException } => !timeLimit.IsCancellationRequested,
            _ => false
        };

        /// <summary>Returns null when the certificate header cannot be used.</summary>
        private HttpClient? SelectClient(HttpRequestData incomingRequest, string targetHost)
        {
            if (!string.IsNullOrEmpty(_settings.CustomCertificateHeaderName)
                && incomingRequest.Headers.TryGetValues(_settings.CustomCertificateHeaderName, out var certificateHeaders))
            {
                _logger.LogInformation("Client certificate provided in header");

                var certificates = certificateHeaders.ToArray();
                if (certificates.Length != 1)
                {
                    _logger.LogWarning("Expected one certificate header, got {Count}", certificates.Length);
                    return null;
                }

                try
                {
                    // Shared client per certificate, so connections are reused between requests.
                    return _clientCertificateHttpClientProvider.GetClient(certificates[0]);
                }
                catch (Exception ex) when (ex is FormatException or CryptographicException)
                {
                    // Only the type is logged. The exception message could quote the header value.
                    _logger.LogWarning("The certificate header could not be read as a certificate ({ExceptionType})", ex.GetType().Name);
                    return null;
                }
            }

            var ignoreValidationForHost = _ignoreValidationHosts.Contains(targetHost, StringComparer.OrdinalIgnoreCase);

            if (_settings.IgnoreCertificateValidation || ignoreValidationForHost)
            {
                _logger.LogInformation("Ignoring certificate validation");
                return _httpClientFactory.CreateClient(Constants.DanProxyNoValidationHttpClient);
            }

            _logger.LogInformation("Running standard proxy setup");
            return _httpClientFactory.CreateClient(Constants.DanProxyHttpClient);
        }

        private void CopyRequestHeaders(HttpRequestData incomingRequest, HttpRequestMessage outgoingRequest)
        {
            var content = outgoingRequest.Content;

            // Connection names the headers that only apply to the connection to the proxy (RFC 9110, section 7.6.1).
            var namedInConnection = incomingRequest.Headers.TryGetValues("Connection", out var connectionValues)
                ? connectionValues.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray()
                : [];

            foreach (var header in incomingRequest.Headers.Where(header =>
                ProxyHeaders.IsForwarded(header.Key, _headersNotForwarded)
                && !namedInConnection.Contains(header.Key.Trim(), StringComparer.OrdinalIgnoreCase)))
            {
                // Headers that describe the body, such as Content-Type, belong to the content and
                // are refused by the request itself. Without a body there is nothing to describe.
                var forwarded = outgoingRequest.Headers.TryAddWithoutValidation(header.Key, header.Value)
                    || (content is not null && content.Headers.TryAddWithoutValidation(header.Key, header.Value));

                if (forwarded && _settings.DebugMode)
                {
                    LogHeader("Outgoing", header.Key, header.Value);
                }
            }

            if (!outgoingRequest.Headers.Contains("Accept"))
            {
                outgoingRequest.Headers.Add("Accept", "application/json");
            }

            if (content is not null && !content.Headers.Contains("Content-Type"))
            {
                content.Headers.TryAddWithoutValidation("Content-Type", DefaultRequestContentType);
            }
        }

        private static void CopyResponseHeaders(HttpResponseMessage targetResponse, HttpResponseData outgoingResponse)
        {
            foreach (var name in ProxyHeaders.ReturnedToCaller)
            {
                if (targetResponse.Headers.TryGetValues(name, out var values) || targetResponse.Content.Headers.TryGetValues(name, out values))
                {
                    outgoingResponse.Headers.TryAddWithoutValidation(name, values);
                }
            }

            // Under another name, so that no caller follows the redirect on its own.
            if (targetResponse.Headers.TryGetValues("Location", out var location))
            {
                outgoingResponse.Headers.TryAddWithoutValidation(Constants.TargetLocationHeader, location);
            }

            if (!outgoingResponse.Headers.Contains("Content-Type"))
            {
                outgoingResponse.Headers.Add("Content-Type", DefaultResponseContentType);
            }
        }

        private void LogHeader(string direction, string name, IEnumerable<string> values)
        {
            // The certificate header holds a private key, also when it has been given the name of a harmless header.
            var isCertificateHeader = !string.IsNullOrEmpty(_settings.CustomCertificateHeaderName)
                && string.Equals(name.Trim(), _settings.CustomCertificateHeaderName.Trim(), StringComparison.OrdinalIgnoreCase);

            var value = !isCertificateHeader && ProxyHeaders.IsValueLogged(name) ? string.Join(",", values) : "[not logged]";
            _logger.LogInformation("{Direction}::: header {HeaderName} : {HeaderValue}", direction, name, value);
        }

        private static async Task<HttpResponseData> ErrorAsync(HttpRequestData incomingRequest, HttpStatusCode statusCode, string error, string message)
        {
            var response = incomingRequest.CreateResponse(statusCode);
            response.Headers.Add(Constants.ProxyErrorHeader, error);
            await response.WriteStringAsync(message);
            return response;
        }
    }
}
