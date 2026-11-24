namespace Dan.ProxyV2.Config
{
    public class DanProxySettings
    {
        public bool DebugMode { get; set; } = false;

        public string Ignored { get; set; } = string.Empty;
        public string[] IgnoredHeaders => SplitList(Ignored);

        /// <summary>
        /// Comma separated list of the hosts the proxy may call, for example
        /// "api.norskpensjon.no, publicsector.dnb.no". When the list has entries, every other
        /// host is refused. An empty list allows every host, which lets anyone with the function
        /// key reach any address from the proxy's IP, including addresses inside the VNet.
        /// </summary>
        public string AllowedTargetHosts { get; set; } = string.Empty;
        public string[] AllowedTargetHostsList => SplitList(AllowedTargetHosts).Select(NormalizeHostEntry).ToArray();

        /// <summary>
        /// How long the proxy waits for the target, from the request is sent until the whole
        /// response is read, every attempt and redirect included. Zero or less means no limit.
        /// </summary>
        public int TargetTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// How long the proxy waits for a connection to the target before it gives the attempt
        /// up. Zero or less means no limit other than <see cref="TargetTimeoutSeconds"/>.
        /// </summary>
        public int ConnectTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// How many times the proxy tries again when it cannot connect to the target, or gets
        /// no connection within <see cref="ConnectTimeoutSeconds"/>. Nothing of the request has
        /// been sent at that point, so the target cannot get the same request twice. Zero turns it off.
        /// </summary>
        public int ConnectRetries { get; set; } = 1;

        // Off by default, so an app where the setting is missing still validates target certificates.
        public bool IgnoreCertificateValidation { get; set; } = false;
        public string CustomCertificateHeaderName { get; set; } = string.Empty;
        /// <summary>
        /// Komma-separert liste over hostnavn der sertifikatvalidering skal ignoreres.
        /// Gjelder kun når IgnoreCertificateValidation = false (ellers ignoreres validering globalt).
        /// Eksempel: "nordicinformation.api.prh.fi, annet.host.no" (Finsk API for NSG).
        /// </summary>
        public string IgnoreCertificateValidationHosts { get; set; } = string.Empty;
        public string[] IgnoreCertificateValidationHostsList =>
            SplitList(IgnoreCertificateValidationHosts).Select(NormalizeHostEntry).ToArray();

        private static string[] SplitList(string? value) =>
            (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // An entry is meant to be a host name, but a port, a scheme or a path is easy to add by
        // mistake. The host name is picked out of such an entry. Read as it stands, "host:443"
        // would be taken for a url with "host" as its scheme and no host at all.
        private static string NormalizeHostEntry(string entry)
        {
            var asUrl = entry.Contains("://", StringComparison.Ordinal) ? entry : "https://" + entry;

            return Uri.TryCreate(asUrl, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.Host)
                ? parsed.Host
                : entry;
        }
    }
}
