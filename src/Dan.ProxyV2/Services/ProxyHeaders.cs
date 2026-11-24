namespace Dan.ProxyV2.Services
{
    /// <summary>Which headers the proxy passes on to the target, and which it passes back to the caller.</summary>
    internal static class ProxyHeaders
    {
        private static readonly HashSet<string> NeverForwarded = new(StringComparer.OrdinalIgnoreCase)
        {
            // Set from the target url.
            "Host",
            // Set from the body that is sent.
            "Content-Length",
            // Hop-by-hop headers describe the connection to the proxy, not the request (RFC 9110, section 7.6.1).
            "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection",
            "TE", "Trailer", "Transfer-Encoding", "Upgrade",
            // Trace context of our own telemetry.
            "traceparent", "tracestate", "baggage", "Request-Id", "Correlation-Context", "Request-Context",
            // Added by App Service and other intermediaries in front of the function.
            "Forwarded", "Via", "Max-Forwards", "CLIENT-IP", "DISGUISED-HOST", "WAS-DEFAULT-HOSTNAME"
        };

        /// <summary>
        /// The response headers a caller needs to read the body and to handle errors. Everything
        /// else the target sends, such as cookies and server banners, stays in the proxy.
        /// Location is not among them. It is returned under another name, see
        /// <see cref="Constants.TargetLocationHeader"/>.
        /// </summary>
        public static readonly string[] ReturnedToCaller =
        [
            "Content-Type", "Content-Encoding", "Retry-After", "WWW-Authenticate"
        ];

        /// <summary>
        /// Headers starting with "x-" are never forwarded. That covers the function key, the
        /// certificate header and everything App Service and API Management add.
        /// </summary>
        public static bool IsForwarded(string headerName, IEnumerable<string> ignoredHeaders)
        {
            var name = headerName.Trim();

            return !name.StartsWith("x-", StringComparison.OrdinalIgnoreCase)
                && !NeverForwarded.Contains(name)
                && !ignoredHeaders.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        // Headers that describe the request and cannot hold a credential.
        private static readonly HashSet<string> ValueMayBeLogged = new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept", "Accept-Charset", "Accept-Encoding", "Accept-Language", "Cache-Control", "Connection",
            "Content-Encoding", "Content-Length", "Content-Type", "Host", "Pragma", "User-Agent"
        };

        /// <summary>
        /// True for the headers whose value may be written to the log in debug mode. All other
        /// values are left out. A list of the headers that hold secrets would miss the ones
        /// nobody thought of: an API key in a header of the caller's own, or the certificate
        /// header when its name is not configured.
        /// </summary>
        public static bool IsValueLogged(string headerName) => ValueMayBeLogged.Contains(headerName.Trim());
    }
}
