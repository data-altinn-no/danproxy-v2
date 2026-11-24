# danproxy-v2

An Azure Function that forwards HTTPS calls from the data.altinn.no plugins to external APIs.
The external APIs only accept calls from a fixed IP address, and the proxy is the app that has it.

This is the second version of [proxy](https://github.com/data-altinn-no/proxy). It is meant to run on a
dedicated App Service plan with deployment slots, beside the old proxy until the plugins have been moved over.

## How it is called

```
GET or POST https://<app>.azurewebsites.net/DanProxy?url=<target>&code=<function key>
```

- `url` is the target without scheme, for example `api.example.no/v1/items?from=2026-01-01`. It must be
  URL encoded once. The proxy always calls the target over HTTPS.
- Method, body and headers are sent on to the target. The body is sent byte for byte.
- A client certificate can be sent as a base64 encoded PFX in the header named by `CustomCertificateHeaderName`.
  Send exactly the same header value on every call, otherwise connections are not reused.
- The PFX must hold the certificate with its private key. If the target needs the certificates of the
  authorities that issued it, put them in the same PFX, without their keys, and the proxy sends them on. It
  never fetches them itself, because the address to fetch from stands in the certificate and is chosen by
  the caller. Norsk Pensjon does not need them.
- The certificate of the target is always validated when a client certificate is sent, whatever
  `IgnoreCertificateValidation` and `IgnoreCertificateValidationHosts` say. That is as in the first proxy.
- The proxy keeps the certificate and its key in memory while they are in use, and never logs them.
  A certificate that has not been used for an hour is dropped.

### Headers

Not sent on to the target:

- headers starting with `x-`, which covers the function key and the certificate header
- hop-by-hop headers such as `Connection` and `Transfer-Encoding`
- trace headers such as `traceparent`
- headers added by App Service, and the ones listed in `Ignored`

Sent back to the caller: `Content-Type`, `Content-Encoding`, `Retry-After` and `WWW-Authenticate`.

### Errors made by the proxy

Every error the proxy makes itself has the header `X-Dan-Proxy-Error`. A response without it comes from the target.

| Status | `X-Dan-Proxy-Error` | Meaning |
|---|---|---|
| 400 | `invalid_url` | `url` is missing or cannot be read as an address |
| 400 | `invalid_certificate` | The certificate header is not one base64 encoded PFX with a private key |
| 403 | `host_not_allowed` | `AllowedTargetHosts` is set and the target host is not in it |
| 502 | `connection_failed` | The target could not be reached, also not on a new attempt, or its certificate was not accepted |
| 504 | `timeout` | The target did not answer within `TargetTimeoutSeconds` |

Redirects are followed by the proxy, as in the first proxy, so the caller gets the final answer. When
`AllowedTargetHosts` is set, a redirect to a host outside the list is refused with 403 `host_not_allowed`.

A redirect the proxy does not follow, such as one from https to http, is returned as a 3xx response with the
address in the header `X-Dan-Proxy-Location`, not in `Location`. A caller that follows redirects would otherwise
call that address itself, and send all its headers there, the certificate header with the private key among them.

When the proxy cannot connect to the target, or gets no connection within `ConnectTimeoutSeconds`, it waits a
quarter of a second and tries again, `ConnectRetries` times. Nothing of the request has been sent at that point,
so the target never gets the same request twice. A failure after a redirect is not tried again, because the
target has then answered the request once. Each failed attempt is logged as a warning with the exception.

## Settings

| Name | Default | Description |
|---|---|---|
| `AllowedTargetHosts` | empty | Comma separated list of the hosts the proxy may call. Empty means every host is allowed |
| `CustomCertificateHeaderName` | empty | Name of the header that carries a client certificate |
| `IgnoreCertificateValidation` | `false` | Accept invalid certificates from every target. Never turn on in production |
| `IgnoreCertificateValidationHosts` | empty | Comma separated list of hosts whose certificate is not validated |
| `Ignored` | empty | Comma separated list of extra headers that are not sent on to the target |
| `TargetTimeoutSeconds` | `30` | Time limit for one call to a target, all attempts and redirects included. Zero or less means no limit |
| `ConnectTimeoutSeconds` | `10` | Time limit for setting up a connection to a target. Zero or less means no limit of its own |
| `ConnectRetries` | `1` | How many times a failed connection to a target is tried again. Zero turns it off |
| `DebugMode` | `false` | Logs header names and request bodies. Header values are only logged for a few harmless headers, such as `Accept`. Bodies are not filtered |

## Changes from the first proxy

- The hosts that can be called can be limited with `AllowedTargetHosts`.
- A redirect to a host outside `AllowedTargetHosts` is refused. A redirect that is not followed is returned
  with the address in `X-Dan-Proxy-Location`.
- A call to a target is given up after 30 seconds, and a connection attempt after 10. Before, the only limit
  was 100 seconds for the whole call.
- Connection failures give 502 and timeouts give 504. Before, both gave an empty 500.
- A failed connection to a target is tried once more before the proxy gives up.
- The proxy no longer fetches the issuer certificates of a client certificate in every handshake with the
  target. The caller sends them in the certificate header.
- A call is stopped when the caller gives up or the time limit is reached.
- `url` is decoded once, not twice, so an encoded `+` or `%` in the target's query string survives.
- Request bodies are sent as bytes, and `Content-Type` is sent unchanged.
- More response headers are returned, and internal request headers are no longer sent to the target.
- Targets without certificate validation reuse connections like the other targets.
- Connections are kept for up to ten minutes for all targets. Cookies from a target are never stored.

## Run and test locally

```
copy src\Dan.ProxyV2\local.settings.json.template src\Dan.ProxyV2\local.settings.json
cd src\Dan.ProxyV2
func start
```

From the repository root:

```
dotnet test
```

The tests start a local HTTPS server and run the proxy against it, so they need no network access.
Four tests of how the chain of a client certificate is handled run on Linux only, and are skipped on Windows.
On Windows the operating system fetches the chain itself and keeps it in the user's certificate store.

For the same reason the proxy must run on Linux in Azure. On a Windows plan it would again call addresses taken
from the certificates callers send.

## Deploy

`.github/workflows/danproxy-v2.build-deploy.yml` uses the shared slot flow in `deploy-actions`: dev, then the
staging slot, then a swap to production. It needs these secrets, set per GitHub environment where they differ:

- `FUNCTIONAPP_NAME`
- `AZURE_FUNCTION_PUBLISH_CREDS`
- `AZURE_CREDENTIALS`
- `RESOURCE_GROUP_PROD`
