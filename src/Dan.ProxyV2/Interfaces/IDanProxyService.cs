using Microsoft.Azure.Functions.Worker.Http;

namespace Dan.ProxyV2.Interfaces;
public interface IDanProxyService
{
    /// <summary>
    /// Sends the request on to the target given in the url query parameter and returns the target's response.
    /// Used to reach data sources that only accept calls from the proxy's fixed IP address.
    /// </summary>
    /// <param name="request">The request as the proxy received it.</param>
    /// <param name="cancellationToken">Cancelled when the caller has given up, which also stops the call to the target.</param>
    public Task<HttpResponseData> ProxyRequest(HttpRequestData request, CancellationToken cancellationToken = default);
}
