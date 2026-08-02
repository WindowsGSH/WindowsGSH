using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WindowsGSH.Core.Web.Auth;

namespace WindowsGSH.Core.Web.Api;

internal static class AdminEndpoints
{
    internal static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").RequireAuthorization("AdminAccess");

        group.MapGet("/users", GetUsersAsync);
        group.MapPost("/users", CreateUserAsync);
        group.MapPatch("/users/{id:int}", PatchUserAsync);
        group.MapDelete("/users/{id:int}", DeleteUserAsync);
        group.MapPost("/users/{id:int}/reset-password", ResetPasswordAsync);
        group.MapGet("/settings", GetSettings);
    }

    private static async Task<IResult> GetUsersAsync(WebTokenService svc)
    {
        var users = await svc.ListUsersAsync();
        return Results.Ok(users.Select(ToDto).ToList());
    }

    private static async Task<IResult> CreateUserAsync(
        AdminCreateUserRequest req,
        IWebUserStore store,
        WebTokenService svc,
        HttpContext ctx)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
            return Results.Problem("Username is required.", statusCode: 400);
        if (!WebPasswordPolicy.IsValid(req.Password))
            return Results.Problem($"Password must be at least {WebPasswordPolicy.MinimumLength} characters.", statusCode: 400);
        if (!Enum.TryParse<WebRole>(req.Role, ignoreCase: true, out var role))
            return Results.Problem($"Invalid role '{req.Role}'. Valid values: Viewer, Operator, Admin.", statusCode: 400);

        if (await store.FindByUsernameAsync(req.Username.Trim()) != null)
            return Results.Problem($"Username '{req.Username.Trim()}' is already taken.", statusCode: 409);

        var callerName = GetCallerName(ctx);
        var user = await svc.CreateUserAsync(req.Username, req.Password!, role);
        if (user == null)
            return Results.Problem("Failed to create user.", statusCode: 400);

        WebLog.Add($"Admin '{callerName}' created user '{user.Username}' with role '{user.Role}'.");
        return Results.Created($"/api/admin/users/{user.Id}", ToDto(user.ToSummary()));
    }

    private static async Task<IResult> PatchUserAsync(
        int id,
        AdminPatchUserRequest req,
        WebTokenService svc,
        HttpContext ctx)
    {
        WebRole? role = null;
        if (!string.IsNullOrEmpty(req.Role))
        {
            if (!Enum.TryParse<WebRole>(req.Role, ignoreCase: true, out var parsedRole))
                return Results.Problem($"Invalid role '{req.Role}'.", statusCode: 400);
            role = parsedRole;
        }

        var callerId = GetCallerId(ctx);
        var callerName = GetCallerName(ctx);
        var error = await svc.AdminUpdateUserAsync(id, callerId, role, req.Enabled, callerName);
        if (error == null) return Results.NoContent();
        return error == "User not found."
            ? Results.Problem(error, statusCode: 404)
            : Results.Problem(error, statusCode: 422);
    }

    private static async Task<IResult> DeleteUserAsync(
        int id,
        WebTokenService svc,
        HttpContext ctx)
    {
        var callerId = GetCallerId(ctx);
        var callerName = GetCallerName(ctx);
        var error = await svc.AdminDeleteUserAsync(id, callerId, callerName);
        if (error == null) return Results.NoContent();
        return error == "User not found."
            ? Results.Problem(error, statusCode: 404)
            : Results.Problem(error, statusCode: 422);
    }

    // P3-02 (accepted risk, documented 2026-07-16): the reset token is returned directly in
    // the JSON response body rather than delivered out-of-band (e.g. email), since this app
    // has no email delivery mechanism and the panel is admin-only/local. This means the token
    // can appear in devtools/proxy logs/API monitoring for whoever has admin access. Mitigated
    // by keeping the token single-use (WebTokenService.GeneratePasswordResetTokenAsync, enforced
    // via IWebUserStore.UsePasswordResetTokenAsync's first-caller-wins semantics) and short-lived
    // (PasswordResetTokenLifetime = 1 hour). Never log the token value itself. If email delivery
    // is ever added, stop returning the plaintext token here.
    private static async Task<IResult> ResetPasswordAsync(
        int id,
        IWebUserStore store,
        WebTokenService svc,
        HttpContext ctx)
    {
        var user = await store.FindByIdAsync(id);
        if (user == null) return Results.Problem("User not found.", statusCode: 404);

        var callerName = GetCallerName(ctx);
        var token = await svc.GeneratePasswordResetTokenAsync(id);
        WebLog.Add($"Admin '{callerName}' generated a password reset token for '{user.Username}'.");
        return Results.Ok(new AdminResetTokenResponse(token));
    }

    private static IResult GetSettings()
    {
        return Results.Ok(new AdminSettingsResponse(
            WebHostService.ActivePort,
            WebHostService.ActiveBindAddress,
            WebHostService.IsRunning));
    }

    private static int GetCallerId(HttpContext ctx)
    {
        var sub = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(sub, out var id) ? id : 0;
    }

    private static string GetCallerName(HttpContext ctx)
        => ctx.User.FindFirstValue(ClaimTypes.Name) ?? "unknown";

    private static AdminUserDto ToDto(WebUserSummary u) =>
        new(u.Id, u.Username, u.Role.ToString(), u.CreatedUtc, u.LastLoginUtc, u.Enabled, u.ForcePasswordChange);
}

public sealed record AdminCreateUserRequest(string? Username, string? Password, string? Role);

public sealed record AdminPatchUserRequest(string? Role, bool? Enabled);

public sealed record AdminUserDto(
    int Id,
    string Username,
    string Role,
    DateTimeOffset CreatedUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastLoginUtc,
    bool Enabled,
    bool ForcePasswordChange);

public sealed record AdminResetTokenResponse(string ResetToken);

public sealed record AdminSettingsResponse(int? Port, string? BindAddress, bool IsRunning);
