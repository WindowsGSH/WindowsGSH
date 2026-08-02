namespace WindowsGSH.Core.Web.Auth;

public static class WebPasswordPolicy
{
    public const int MinimumLength = 12;

    public static bool IsValid(string? password) =>
        !string.IsNullOrWhiteSpace(password) && password.Length >= MinimumLength;
}
