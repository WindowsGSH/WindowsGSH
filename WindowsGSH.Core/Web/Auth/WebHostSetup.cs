using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using WindowsGSH.Core.Diagnostics;
using WindowsGSH.Core.Web.Api;

namespace WindowsGSH.Core.Web.Auth;

public static class WebHostSetup
{
    /// <summary>
    /// Returns service-registration and pipeline-configuration callbacks ready to pass to
    /// <see cref="WebHostService.TryStartAsync"/>. Auth endpoints and JWT bearer middleware
    /// are fully self-contained in the returned delegates.
    /// </summary>
    public static (Action<WebApplicationBuilder> ConfigureServices, Action<WebApplication> ConfigurePipeline, WebTokenService TokenService)
        CreateAuth(IWebUserStore store, byte[] signingKey, bool trustForwardedHeaders = false)
    {
        var tokenService = new WebTokenService(store, signingKey);

        return (
            ConfigureServices: builder =>
            {
                builder.Services.AddSingleton<IWebUserStore>(store);
                builder.Services.AddSingleton(tokenService);
                builder.Services.AddAuthentication("Bearer")
                    .AddScheme<AuthenticationSchemeOptions, WebJwtBearerHandler>("Bearer", null);
                builder.Services.AddRateLimiter(rl =>
                {
                    // Per-IP fixed-window limiter: each source address gets its own
                    // bucket, so one client cannot exhaust the allowance for others.
                    rl.AddPolicy("login", ctx =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                            factory: _ => new FixedWindowRateLimiterOptions
                            {
                                Window = TimeSpan.FromMinutes(5),
                                PermitLimit = 10,
                                QueueLimit = 0,
                            }));
                    rl.RejectionStatusCode = 429;
                });
                builder.Services.AddAuthorization(opts =>
                {
                    // "FullAccess": valid token without force-password-change claim.
                    opts.AddPolicy("FullAccess", policy =>
                        policy.RequireAuthenticatedUser()
                              .RequireAssertion(ctx => !ctx.User.HasClaim("fpc", "1")));

                    // "OperatorAccess": FullAccess AND role >= Operator.
                    opts.AddPolicy("OperatorAccess", policy =>
                        policy.RequireAuthenticatedUser()
                              .RequireAssertion(ctx =>
                                  !ctx.User.HasClaim("fpc", "1") &&
                                  GetRole(ctx.User) >= WebRole.Operator));

                    // "AdminAccess": FullAccess AND role == Admin.
                    opts.AddPolicy("AdminAccess", policy =>
                        policy.RequireAuthenticatedUser()
                              .RequireAssertion(ctx =>
                                  !ctx.User.HasClaim("fpc", "1") &&
                                  GetRole(ctx.User) == WebRole.Admin));
                });
            },
            ConfigurePipeline: app =>
            {
                var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

                if (trustForwardedHeaders)
                {
                    app.UseForwardedHeaders(new ForwardedHeadersOptions
                    {
                        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                        KnownProxies = { IPAddress.Loopback, IPAddress.IPv6Loopback },
                    });
                }

                app.UseRateLimiter();

                // Security headers on every response (including static assets).
                app.Use(async (ctx, next) =>
                {
                    ctx.Response.Headers.Append("Content-Security-Policy",
                        "default-src 'self'; " +
                        "script-src 'self'; " +
                        "style-src 'self'; " +
                        "connect-src 'self' ws: wss:; " +
                        "img-src 'self' data:; " +
                        "frame-ancestors 'none'");
                    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
                    ctx.Response.Headers.Append("X-Frame-Options", "DENY");
                    ctx.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
                    ctx.Response.Headers.Append("Cross-Origin-Opener-Policy", "same-origin");
                    await next();
                });

                // Serve static assets (CSS, JS, images) from wwwroot/.
                if (Directory.Exists(wwwroot))
                {
                    app.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(wwwroot),
                        RequestPath = "",
                        ContentTypeProvider = BuildContentTypeProvider(),
                    });
                }

                app.UseWebSockets();
                app.UseAuthentication();
                app.UseAuthorization();

                app.MapAuthEndpoints();
                app.MapServerStatusEndpoints();
                app.MapServerControlEndpoints();
                app.MapServerConsoleEndpoints();
                app.MapAdminEndpoints();

                // Authenticated status — version disclosure kept behind auth so passive
                // observers cannot use the version string to target known CVEs.
                app.MapGet("/api/status", () => Results.Ok(new
                {
                    version = AppVersionInfo.DisplayVersion,
                    utc = DateTime.UtcNow.ToString("O"),
                })).RequireAuthorization("FullAccess");

                MapPageRoutes(app, wwwroot);
            },
            TokenService: tokenService);
    }

    // P3-08 (documented, not fixed): each page route below is deliberately AllowAnonymous
    // and returns the static HTML file unconditionally. Auth and role checks happen entirely
    // client-side in JavaScript after the page loads (tokens are never embedded in the HTML
    // response). This means an unauthenticated request can always fetch the page shell itself
    // — just not any data, since every /api/* endpoint underneath enforces its own
    // authorization independently. API auth is the authoritative boundary here, not page
    // routing; do not add anything data-bearing to these page responses without adding
    // server-side auth to match.
    private static void MapPageRoutes(WebApplication app, string wwwroot)
    {
        IResult ServePage(string fileName)
        {
            var path = Path.Combine(wwwroot, fileName);
            if (!File.Exists(path))
                return Results.NotFound();
            return Results.Content(File.ReadAllText(path, Encoding.UTF8), "text/html; charset=utf-8");
        }

        app.MapGet("/", () => ServePage("index.html")).AllowAnonymous();
        app.MapGet("/dashboard", () => ServePage("dashboard.html")).AllowAnonymous();
        app.MapGet("/servers/{id}", (string id) => ServePage("server.html")).AllowAnonymous();
        app.MapGet("/servers/{id}/console", (string id) => ServePage("console.html")).AllowAnonymous();
        app.MapGet("/admin/users", () => ServePage("admin/users.html")).AllowAnonymous();
        app.MapGet("/admin/settings", () => ServePage("admin/settings.html")).AllowAnonymous();
    }

    private static FileExtensionContentTypeProvider BuildContentTypeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        // Ensure .js modules are served with the correct MIME type.
        provider.Mappings[".js"] = "application/javascript";
        provider.Mappings[".css"] = "text/css";
        return provider;
    }

    private static WebRole GetRole(System.Security.Claims.ClaimsPrincipal user)
    {
        var roleStr = user.FindFirst(ClaimTypes.Role)?.Value;
        return Enum.TryParse<WebRole>(roleStr, out var r) ? r : WebRole.Viewer;
    }
}
