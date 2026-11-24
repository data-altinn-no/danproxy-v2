using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Dan.ProxyV2.UnitTest.TestSupport
{
    /// <summary>The proxy never reads the function context, it is only needed to construct request and response.</summary>
    internal sealed class FakeFunctionContext : FunctionContext
    {
        public override string InvocationId => "test-invocation";
        public override string FunctionId => "test-function";
        public override TraceContext TraceContext => throw new NotSupportedException();
        public override BindingContext BindingContext => throw new NotSupportedException();
        public override Microsoft.Azure.Functions.Worker.RetryContext RetryContext => throw new NotSupportedException();
        public override IServiceProvider InstanceServices { get; set; } = null!;
        public override FunctionDefinition FunctionDefinition => throw new NotSupportedException();
        public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
        public override IInvocationFeatures Features => throw new NotSupportedException();
    }

    internal sealed class FakeHttpRequestData : HttpRequestData
    {
        public FakeHttpRequestData(Uri url, string method = "GET", byte[]? body = null) : base(new FakeFunctionContext())
        {
            Url = url;
            Method = method;
            Body = new MemoryStream(body ?? []);
        }

        public override Stream Body { get; }
        public override HttpHeadersCollection Headers { get; } = new();
        public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = Array.Empty<IHttpCookie>();
        public override Uri Url { get; }
        public override IEnumerable<ClaimsIdentity> Identities { get; } = Array.Empty<ClaimsIdentity>();
        public override string Method { get; }

        public override HttpResponseData CreateResponse() => new FakeHttpResponseData(FunctionContext);
    }

    internal sealed class FakeHttpResponseData : HttpResponseData
    {
        public FakeHttpResponseData(FunctionContext functionContext) : base(functionContext)
        {
        }

        public override HttpStatusCode StatusCode { get; set; }
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies => throw new NotSupportedException();

        public byte[] BodyAsBytes() => ((MemoryStream)Body).ToArray();

        public string BodyAsString() => Encoding.UTF8.GetString(BodyAsBytes());

        /// <summary>The header's value, or null when the response does not have it.</summary>
        public string? Header(string name) =>
            Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }
}
