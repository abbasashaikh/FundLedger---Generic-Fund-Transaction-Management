using System.Net;
using FundLedger.Application.Abstractions;

namespace FundLedger.Api.Hosting;

/// <summary>Request metadata for audit records and session rows.</summary>
internal sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public string? RequestId => accessor.HttpContext?.TraceIdentifier;

    public IPAddress? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress;

    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
}
