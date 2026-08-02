using WindowsGSH.Core.Web.Auth;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class WebPasswordPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("            ", false)]
    [InlineData("12345678901", false)]
    [InlineData("123456789012", true)]
    public void IsValid_EnforcesSharedMinimum(string? password, bool expected)
    {
        Assert.Equal(12, WebPasswordPolicy.MinimumLength);
        Assert.Equal(expected, WebPasswordPolicy.IsValid(password));
    }
}
