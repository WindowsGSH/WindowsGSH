using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WindowsGSH.Core.Web.Auth;

public sealed class WebJwtBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly WebTokenService _tokenService;

    public WebJwtBearerHandler(
        WebTokenService tokenService,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _tokenService = tokenService;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (authHeader == null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = authHeader["Bearer ".Length..].Trim();
        if (!_tokenService.TryValidateAccessToken(token, out var sub, out var username, out var role, out var forcePasswordChange))
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired token."));

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, sub!),
            new Claim(ClaimTypes.Name, username!),
            new Claim(ClaimTypes.Role, role!.Value.ToString()),
        };
        if (forcePasswordChange)
            claims.Add(new Claim("fpc", "1"));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
