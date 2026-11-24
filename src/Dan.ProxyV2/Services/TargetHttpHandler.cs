using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using Dan.ProxyV2.Config;

namespace Dan.ProxyV2.Services
{
    /// <summary>Thrown when the proxy is about to connect to a host that is not in AllowedTargetHosts.</summary>
    internal sealed class TargetHostNotAllowedException(string host) : Exception($"The host {host} is not in AllowedTargetHosts.")
    {
        public string Host { get; } = host;
    }

    /// <summary>
    /// Builds the handlers the proxy calls targets with. All three kinds of client (standard,
    /// without certificate validation, with client certificate) are built from this one.
    /// </summary>
    internal static class TargetHttpHandler
    {
        // Connections are recycled after this long so that DNS changes are picked up.
        internal static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(10);

        // Kept below the 4 minute idle timeout of Azure outbound NAT.
        internal static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(2);

        /// <param name="configure">Lets tests adjust the handler, for example to trust a local test server.</param>
        public static SocketsHttpHandler Create(DanProxySettings settings, Action<SocketsHttpHandler>? configure = null)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = ConnectionLifetime,
                PooledConnectionIdleTimeout = ConnectionIdleTimeout,
                // The handler is shared between requests, so it must not carry cookies from one request to the next.
                UseCookies = false,
                // Redirects are followed here, as in the first proxy, so the caller gets the final answer.
                AllowAutoRedirect = true,
                // The proxy's trace ids belong in our own telemetry, not in calls to other organisations.
                // A propagator that writes nothing keeps the call itself in the telemetry. With no
                // propagator at all, the handler reports no activity, and the call is missing there.
                ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator()
            };

            if (settings.ConnectTimeoutSeconds > 0)
            {
                handler.ConnectTimeout = TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds);
            }

            // The service checks the host the caller asks for. A redirect is followed inside the
            // handler, so the host is checked once more here, for every connection that is made.
            // Otherwise a target could send the proxy on to a host outside the list.
            var allowedHosts = settings.AllowedTargetHostsList;
            if (allowedHosts.Length > 0)
            {
                handler.ConnectCallback = (context, cancellationToken) => ConnectToAllowedHostAsync(allowedHosts, context, cancellationToken);
            }

            configure?.Invoke(handler);
            return handler;
        }

        /// <summary>
        /// A handler that accepts an invalid certificate from every target when
        /// IgnoreCertificateValidation is on, and otherwise only from the hosts in
        /// IgnoreCertificateValidationHosts. The handler is shared between targets, so the host
        /// is checked for each connection.
        /// </summary>
        public static SocketsHttpHandler CreateWithoutCertificateValidation(DanProxySettings settings, Action<SocketsHttpHandler>? configure = null)
        {
            var handler = Create(settings, configure);
            var ignoredHosts = settings.IgnoreCertificateValidationHostsList;

            handler.SslOptions.RemoteCertificateValidationCallback = (sender, _, _, policyErrors) =>
            {
                if (policyErrors == SslPolicyErrors.None || settings.IgnoreCertificateValidation)
                {
                    return true;
                }

                var targetHost = (sender as SslStream)?.TargetHostName;
                return !string.IsNullOrEmpty(targetHost)
                    && ignoredHosts.Contains(targetHost, StringComparer.OrdinalIgnoreCase);
            };

            return handler;
        }

        // Connects the way the handler does on its own, after the host has been checked.
        private static async ValueTask<Stream> ConnectToAllowedHostAsync(
            string[] allowedHosts, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            // The handler gives an IPv6 address in brackets here. The list has it without them.
            var host = context.DnsEndPoint.Host.TrimStart('[').TrimEnd(']');
            if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                throw new TargetHostNotAllowedException(host);
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
        }
    }
}
