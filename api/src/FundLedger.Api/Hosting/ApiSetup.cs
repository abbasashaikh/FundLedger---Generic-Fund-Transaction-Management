using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FundLedger.Api.Endpoints;
using FundLedger.Api.Middleware;
using FundLedger.Application;
using FundLedger.Application.Abstractions;
using FundLedger.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;

namespace FundLedger.Api.Hosting;

/// <summary>Named rate-limit policies (TR-013/TR-073).</summary>
internal static class RateLimitPolicies
{
    /// <summary>Per client IP: 20 login attempts per 15 minutes (per-mobile lockout is in AuthService).</summary>
    public const string Login = "login";
}

internal static class ApiSetup
{
    public const string CorsPolicy = "pwa";

    public static WebApplicationBuilder AddFundLedgerApi(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;

        builder.Host.UseSerilog(LoggingSetup.Configure);

        var sentryDsn = config["Sentry:Dsn"];
        if (!string.IsNullOrWhiteSpace(sentryDsn))
        {
            builder.WebHost.UseSentry(o =>
            {
                o.Dsn = sentryDsn;
                o.Environment = builder.Environment.EnvironmentName;
                o.Release = AppVersion.Current.Version;
                o.SendDefaultPii = false;               // TRD TR-096: user id only, never phone/PIN
                o.TracesSampleRate = 0.1;
            });
        }

        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; // TR-074
            o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;   // numbers must be JSON numbers (and are typed so in OpenAPI)
            // Enums on the wire match the database labels: "ADMIN", "MONEY_IN", ...
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        });

        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
            ctx.ProblemDetails.Extensions.TryAdd("code", CodeFor(ctx.ProblemDetails.Status));
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddOpenApi("v1");

        services.AddFundLedgerApplication();
        services.AddFundLedgerInfrastructure(config, sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            return env.IsDevelopment() || env.IsEnvironment("Testing");
        });
        services.AddHttpContextAccessor();
        services.AddScoped<IRequestContext, HttpRequestContext>();
        services.AddFundLedgerAuth();
        services.AddHostedService<StartupChecks>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        // TR-071: hard-coded allowlist per environment, never a wildcard with credentials.
        var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
            .WithOrigins(origins)
            .AllowCredentials()
            .WithMethods("GET", "POST", "PUT", "PATCH")
            .WithHeaders("Authorization", "Content-Type", "If-Match", "Idempotency-Key", "X-Request-Id", AuthEndpoints.ClientHeader)
            .WithExposedHeaders("ETag", "X-Request-Id", "Retry-After")));

        // TR-073: per-client limit; stricter auth/export policies are added in their phases.
        var permitsPerMinute = config.GetValue("RateLimiting:PermitsPerMinute", 300);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                ctx.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal)
                    ? RateLimitPartition.GetNoLimiter("health")
                    : RateLimitPartition.GetFixedWindowLimiter(
                        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = permitsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        }));
            o.AddPolicy(RateLimitPolicies.Login, ctx => RateLimitPartition.GetFixedWindowLimiter(
                "login:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = config.GetValue("RateLimiting:LoginPerIpPer15Min", 20),
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0,
                }));
            o.OnRejected = async (ctx, ct) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var problems = ctx.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = ctx.HttpContext,
                    ProblemDetails = { Status = 429, Title = "Too many requests. Please try again shortly." },
                }).ConfigureAwait(false);
            };
        });

        // Behind Caddy on the VPS: trust X-Forwarded-* only from private networks.
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
            o.KnownProxies.Add(IPAddress.Loopback);
        });

        services.AddHsts(o =>
        {
            o.MaxAge = TimeSpan.FromDays(365);
            o.IncludeSubDomains = true;
        });

        return builder;
    }

    public static WebApplication UseFundLedgerPipeline(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<RequestIdMiddleware>();
        app.UseSerilogRequestLogging(o => o.EnrichDiagnosticContext = (diag, http) =>
            diag.Set("RequestId", http.TraceIdentifier));
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseCors(CorsPolicy);
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<SessionValidationMiddleware>();
        app.UseAuthorization();
        return app;
    }

    public static WebApplication MapFundLedgerEndpoints(this WebApplication app)
    {
        HealthEndpoints.Map(app);
        SystemEndpoints.Map(app);
        AuthEndpoints.Map(app);
        UserEndpoints.Map(app);

        // The OpenAPI document is published outside production only (TRD §11.1).
        if (!app.Environment.IsProduction())
        {
            app.MapOpenApi("/api/{documentName}/openapi.json");
        }

        return app;
    }

    internal static string CodeFor(int? status) => status switch
    {
        400 => "VALIDATION_FAILED",
        401 => "UNAUTHENTICATED",
        403 => "FORBIDDEN",
        404 => "NOT_FOUND",
        405 => "METHOD_NOT_ALLOWED",
        409 => "CONFLICT",
        412 => "REVISION_CONFLICT",
        429 => "RATE_LIMITED",
        _ => "INTERNAL_ERROR",
    };
}
