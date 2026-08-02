using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using WindowsGSH.Core.Operations;

namespace WindowsGSH.Core.Web.Api;

public static class ServerControlEndpoints
{
    public static void MapServerControlEndpoints(this WebApplication app)
    {
        app.MapPost("/api/servers/{id}/start",      (string id, HttpContext ctx) => Dispatch(id, WebControlKind.Start,     ctx)).RequireAuthorization("OperatorAccess");
        app.MapPost("/api/servers/{id}/stop",       (string id, HttpContext ctx) => Dispatch(id, WebControlKind.Stop,      ctx)).RequireAuthorization("OperatorAccess");
        app.MapPost("/api/servers/{id}/restart",    (string id, HttpContext ctx) => Dispatch(id, WebControlKind.Restart,   ctx)).RequireAuthorization("OperatorAccess");
        app.MapPost("/api/servers/{id}/force-stop", (string id, HttpContext ctx) => DispatchForceStop(id, ctx)).RequireAuthorization("OperatorAccess");
        app.MapPost("/api/servers/{id}/update",     (string id, HttpContext ctx) => Dispatch(id, WebControlKind.Update,    ctx)).RequireAuthorization("OperatorAccess");
        app.MapPost("/api/servers/{id}/cancel",     (string id, HttpContext ctx) => DispatchCancel(id, ctx)).RequireAuthorization("OperatorAccess");
        app.MapGet("/api/servers/{id}/operation",   (string id) => GetOperationStatus(id)).RequireAuthorization("FullAccess");
    }

    private static IResult Dispatch(string id, WebControlKind kind, HttpContext ctx)
    {
        var username = CallerName(ctx);
        var outcome = WebServerControl.TryDispatch(id, kind, username);
        return outcome.Status switch
        {
            WebControlStatus.Accepted     => Results.StatusCode(202),
            WebControlStatus.Conflict     => Results.Conflict(new { conflict = outcome.ConflictingKind }),
            WebControlStatus.NotFound     => Results.NotFound(),
            WebControlStatus.Unavailable  => Results.StatusCode(503),
            _ => Results.StatusCode(500),
        };
    }

    private static IResult DispatchForceStop(string id, HttpContext ctx)
    {
        var username = CallerName(ctx);
        var outcome = WebServerControl.TryDispatch(id, WebControlKind.ForceStop, username);
        return outcome.Status switch
        {
            WebControlStatus.Accepted => Results.Json(
                new { warning = "Force stop terminates the server process immediately. Unsaved data may be lost." },
                statusCode: 202),
            WebControlStatus.Conflict     => Results.Conflict(new { conflict = outcome.ConflictingKind }),
            WebControlStatus.NotFound     => Results.NotFound(),
            WebControlStatus.Unavailable  => Results.StatusCode(503),
            _ => Results.StatusCode(500),
        };
    }

    private static IResult DispatchCancel(string id, HttpContext ctx)
    {
        var username = CallerName(ctx);
        var outcome = WebServerControl.TryDispatch(id, WebControlKind.Cancel, username);
        return outcome.Status switch
        {
            WebControlStatus.Accepted       => Results.Ok(new { cancelled = true }),
            WebControlStatus.NothingToCancel => Results.NotFound(new { message = "No active operation to cancel." }),
            WebControlStatus.NotFound       => Results.NotFound(),
            _ => Results.StatusCode(500),
        };
    }

    private static IResult GetOperationStatus(string id)
    {
        if (!WebServerState.GetServers().Any(s => s.Id == id))
            return Results.NotFound();

        var op = ServerOperationManager.Shared.Get(id);
        return Results.Ok(new OperationStatusResponse(
            OperationId:     op?.OperationId ?? string.Empty,
            Type:            op?.Kind.ToString() ?? string.Empty,
            StartedAt:       op?.StartedAt,
            ProgressMessage: op?.Status ?? "No operation running",
            IsComplete:      op == null || !op.IsActive,
            Succeeded:       op != null && !op.IsActive && op.Status == "Completed"));
    }

    private static string CallerName(HttpContext ctx)
        => ctx.User.FindFirst(ClaimTypes.Name)?.Value ?? "web";
}

public sealed record OperationStatusResponse(
    string OperationId,
    string Type,
    DateTimeOffset? StartedAt,
    string ProgressMessage,
    bool IsComplete,
    bool Succeeded);
