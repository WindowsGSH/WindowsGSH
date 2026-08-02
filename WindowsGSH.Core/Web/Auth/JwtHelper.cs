using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsGSH.Core.Web.Auth;

public static class JwtHelper
{
    private const string Issuer    = "windowsgsh";
    private const string Audience  = "windowsgsh-web";
    private const string Algorithm = "HS256";

    private static readonly byte[] HeaderBytes =
        JsonSerializer.SerializeToUtf8Bytes(new { alg = Algorithm, typ = "JWT" });

    public static string Create(
        byte[] signingKey, string sub, string username, WebRole role,
        TimeSpan lifetime, bool forcePasswordChange = false)
    {
        var header = Base64UrlEncode(HeaderBytes);

        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object>
        {
            ["sub"]      = sub,
            ["username"] = username,
            ["role"]     = role.ToString(),
            ["iat"]      = now.ToUnixTimeSeconds(),
            ["exp"]      = now.Add(lifetime).ToUnixTimeSeconds(),
            ["jti"]      = Guid.NewGuid().ToString("N"),
            ["iss"]      = Issuer,
            ["aud"]      = Audience,
        };
        // Stamp tokens that require a password change — callers use this to deny
        // access to any endpoint beyond /api/auth/change-password.
        if (forcePasswordChange)
            payload["fpc"] = 1;

        var payloadPart = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var sig = Sign(signingKey, $"{header}.{payloadPart}");
        return $"{header}.{payloadPart}.{sig}";
    }

    /// <summary>Backward-compatible overload — does not surface the force-password-change claim.</summary>
    public static bool TryValidate(
        string token, byte[] signingKey,
        out string? sub, out string? username, out WebRole? role)
        => TryValidate(token, signingKey, out sub, out username, out role, out _);

    public static bool TryValidate(
        string token, byte[] signingKey,
        out string? sub, out string? username, out WebRole? role, out bool forcePasswordChange)
    {
        sub = null;
        username = null;
        role = null;
        forcePasswordChange = false;

        var parts = token.Split('.');
        if (parts.Length != 3)
            return false;

        try
        {
            // Verify the HMAC first — everything else is contingent on the signature being valid.
            var expectedSig = Sign(signingKey, $"{parts[0]}.{parts[1]}");
            var actualSig = Base64UrlDecode(parts[2]);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromBase64String(PadBase64(expectedSig.Replace('-', '+').Replace('_', '/'))),
                    actualSig))
            {
                return false;
            }

            // Assert the declared algorithm — rejects alg:none and unexpected algorithm identifiers.
            // The HMAC above already enforces HS256 semantics; this closes the alg-confusion surface.
            var headerJson = Base64UrlDecode(parts[0]);
            using var headerDoc = JsonDocument.Parse(headerJson);
            var alg = headerDoc.RootElement.TryGetProperty("alg", out var algEl)
                ? algEl.GetString()
                : null;
            if (!string.Equals(alg, Algorithm, StringComparison.Ordinal))
                return false;

            var payloadJson = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("exp", out var expEl) &&
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expEl.GetInt64())
            {
                return false;
            }

            // Reject tokens not issued by this service or not intended for the web audience.
            // Prevents cross-context token reuse if the signing key is ever shared.
            var iss = root.TryGetProperty("iss", out var issEl) ? issEl.GetString() : null;
            if (!string.Equals(iss, Issuer, StringComparison.Ordinal))
                return false;

            var aud = root.TryGetProperty("aud", out var audEl) ? audEl.GetString() : null;
            if (!string.Equals(aud, Audience, StringComparison.Ordinal))
                return false;

            sub = root.TryGetProperty("sub", out var subEl) ? subEl.GetString() : null;
            username = root.TryGetProperty("username", out var unEl) ? unEl.GetString() : null;

            if (root.TryGetProperty("role", out var roleEl) &&
                Enum.TryParse<WebRole>(roleEl.GetString(), out var parsedRole))
            {
                role = parsedRole;
            }

            forcePasswordChange = root.TryGetProperty("fpc", out var fpcEl)
                && fpcEl.ValueKind == JsonValueKind.Number
                && fpcEl.GetInt32() == 1;

            return sub != null && username != null && role != null;
        }
        catch
        {
            return false;
        }
    }

    private static string Sign(byte[] key, string message)
    {
        using var hmac = new HMACSHA256(key);
        var sig = hmac.ComputeHash(Encoding.ASCII.GetBytes(message));
        return Base64UrlEncode(sig);
    }

    public static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
        => Convert.FromBase64String(PadBase64(input.Replace('-', '+').Replace('_', '/')));

    private static string PadBase64(string s) => (s.Length % 4) switch
    {
        2 => s + "==",
        3 => s + "=",
        _ => s,
    };
}
