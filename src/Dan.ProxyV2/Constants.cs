namespace Dan.ProxyV2
{
    public static class Constants
    {
        /// <summary>Client for targets whose certificate is validated.</summary>
        public const string DanProxyHttpClient = "DanProxyHttpClient";

        /// <summary>Client for the targets where certificate validation is turned off in the settings.</summary>
        public const string DanProxyNoValidationHttpClient = "DanProxyNoValidationHttpClient";

        /// <summary>
        /// Set on every error response the proxy makes itself, so a caller can tell
        /// "the proxy could not reach the target" from "the target answered with an error".
        /// </summary>
        public const string ProxyErrorHeader = "X-Dan-Proxy-Error";

        /// <summary>
        /// Carries the Location header of a redirect from the target. It is not returned as
        /// Location, because a caller that follows redirects would then call that address
        /// itself and send all its headers there, the certificate header with the private key
        /// among them.
        /// </summary>
        public const string TargetLocationHeader = "X-Dan-Proxy-Location";
    }
}
