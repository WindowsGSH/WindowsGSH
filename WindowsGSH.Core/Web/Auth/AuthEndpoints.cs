using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace WindowsGSH.Core.Web.Auth;

public static class AuthEndpoints
{
    // HttpOnly cookie name for the refresh token used by the browser UI.
    // The cookie path is scoped to /api/auth so it is not sent to other endpoints.
    private const string RefreshCookieName = "gsh_rt";
    private const string ClientHeaderName = "X-WindowsGSH-Client";
    private const string BrowserClientHeaderValue = "browser";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", HandleLogin).AllowAnonymous().RequireRateLimiting("login");
        app.MapPost("/api/auth/refresh", HandleRefresh).AllowAnonymous();
        // Logout only needs the refresh token — no Bearer required. This allows logout even
        // when the access token has already expired, which is the common real-world case.
        app.MapPost("/api/auth/logout", HandleLogout).AllowAnonymous();
        app.MapPost("/api/auth/change-password", HandleChangePassword).RequireAuthorization();
        app.MapPost("/api/auth/reset-password", HandleResetPassword).AllowAnonymous().RequireRateLimiting("login");
    }

    private static async Task<IResult> HandleLogin(
        [FromBody] LoginRequest req,
        HttpContext ctx,
        WebTokenService tokenService)
    {
        var result = await tokenService.LoginAsync(req.Username ?? string.Empty, req.Password ?? string.Empty);
        if (!result.Succeeded)
            return Results.Problem(result.Error, statusCode: 401);

        // Set an HttpOnly cookie so browser clients can use the refresh token without
        // storing it in JavaScript-accessible storage (XSS mitigation).
        // API clients still receive the token in the JSON body — backward-compatible.
        if (!result.ForcePasswordChange && result.RefreshToken != null)
        {
            ctx.Response.Cookies.Append(RefreshCookieName, result.RefreshToken, CreateRefreshCookieOptions(ctx));
        }

        var responseRefreshToken = IsBrowserClient(ctx)
            ? null
            : result.RefreshToken;

        return Results.Ok(new LoginResponse(
            result.AccessToken!,
            responseRefreshToken,
            result.Role!.Value.ToString(),
            result.ExpiresAt!.Value,
            result.ForcePasswordChange));
    }

    private static async Task<IResult> HandleRefresh(
        [FromBody] RefreshRequest req,
        HttpContext ctx,
        WebTokenService tokenService)
    {
        // Accept refresh token from request body (API clients) or from the HttpOnly cookie
        // (browser UI clients). Body takes precedence so API clients are unaffected.
        var requestBodyToken = !string.IsNullOrEmpty(req.RefreshToken);
        var token = req.RefreshToken;
        if (!requestBodyToken)
            token = ctx.Request.Cookies[RefreshCookieName];

        var result = await tokenService.RefreshAsync(token ?? string.Empty);
        if (!result.Succeeded)
            return Results.Problem(result.Error, statusCode: 401);

        // Rotate the cookie when it was the source of the token.
        if (!requestBodyToken && result.RefreshToken != null)
        {
            ctx.Response.Cookies.Append(RefreshCookieName, result.RefreshToken, CreateRefreshCookieOptions(ctx));
        }

        return Results.Ok(new RefreshResponse(
            result.AccessToken!,
            requestBodyToken ? result.RefreshToken : null,
            result.ExpiresAt!.Value));
    }

    private static async Task<IResult> HandleLogout(
        [FromBody] LogoutRequest req,
        HttpContext ctx,
        WebTokenService tokenService)
    {
        var token = req.RefreshToken;
        if (string.IsNullOrEmpty(token))
            token = ctx.Request.Cookies[RefreshCookieName];

        await tokenService.LogoutAsync(token ?? string.Empty);

        // Clear the browser cookie if present.
        ctx.Response.Cookies.Delete(RefreshCookieName, new CookieOptions { Path = "/api/auth", Secure = ShouldUseSecureRefreshCookie(ctx) });

        return Results.NoContent();
    }

    private static CookieOptions CreateRefreshCookieOptions(HttpContext ctx) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth",
        // Mirror the security of the incoming request: true over HTTPS, false over HTTP.
        // When TLS is added to the web host this automatically becomes Secure without any
        // code change — no separate configuration property needed.
        Secure = ShouldUseSecureRefreshCookie(ctx),
        Expires = DateTimeOffset.UtcNow.AddDays(7),
    };

    internal static bool ShouldUseSecureRefreshCookie(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Request.IsHttps;
    }

    internal static bool IsBrowserClient(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Request.Headers.TryGetValue(ClientHeaderName, out var values) &&
            values.Any(value => string.Equals(value, BrowserClientHeaderValue, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IResult> HandleChangePassword(
        HttpContext ctx,
        [FromBody] ChangePasswordRequest req,
        WebTokenService tokenService)
    {
        var subClaim = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(subClaim, out var userId))
            return Results.Unauthorized();

        var error = await tokenService.ChangePasswordAsync(userId, req.CurrentPassword ?? string.Empty, req.NewPassword ?? string.Empty);
        if (error != null)
            return Results.Problem(error, statusCode: 400);

        return Results.NoContent();
    }

    private static async Task<IResult> HandleResetPassword(
        [FromBody] ResetPasswordRequest req,
        WebTokenService tokenService)
    {
        var error = await tokenService.UsePasswordResetAsync(req.ResetToken ?? string.Empty, req.NewPassword ?? string.Empty);
        if (error != null)
            return Results.Problem(error, statusCode: 400);
        return Results.NoContent();
    }
}

public sealed record LoginRequest(string? Username, string? Password);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record LogoutRequest(string? RefreshToken);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record ResetPasswordRequest(string? ResetToken, string? NewPassword);

public sealed record LoginResponse(
    string AccessToken,
    string? RefreshToken,
    string Role,
    DateTimeOffset ExpiresAt,
    bool ForcePasswordChange);

public sealed record RefreshResponse(
    string AccessToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RefreshToken,
    DateTimeOffset ExpiresAt);
