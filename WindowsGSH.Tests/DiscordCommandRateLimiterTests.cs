using WindowsGSH.Core.Discord;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class DiscordCommandRateLimiterTests
{
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-05-22T12:00:00+00:00");

    [Fact]
    public void Allows_first_command()
    {
        var limiter = CreateLimiter();

        Assert.True(limiter.TryAcquire("user-1", "status", "1", out var retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    [Fact]
    public void Rate_limits_repeated_read_only_command_for_same_user_command_and_server()
    {
        var limiter = CreateLimiter();

        Assert.True(limiter.TryAcquire("user-1", "status", "1", out _));

        Assert.False(limiter.TryAcquire("user-1", "status", "1", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Allows_same_command_for_different_server()
    {
        var limiter = CreateLimiter();

        Assert.True(limiter.TryAcquire("user-1", "status", "1", out _));

        Assert.True(limiter.TryAcquire("user-1", "status", "2", out _));
    }

    [Fact]
    public void Allows_same_command_for_different_user()
    {
        var limiter = CreateLimiter();

        Assert.True(limiter.TryAcquire("user-1", "status", "1", out _));

        Assert.True(limiter.TryAcquire("user-2", "status", "1", out _));
    }

    [Fact]
    public void Read_only_command_is_allowed_after_two_second_cooldown()
    {
        var limiter = CreateLimiter();
        Assert.True(limiter.TryAcquire("user-1", "status", "1", out _));

        _now = _now.AddSeconds(2);

        Assert.True(limiter.TryAcquire("user-1", "status", "1", out _));
    }

    [Fact]
    public void Destructive_command_uses_stricter_cooldown()
    {
        var limiter = CreateLimiter();
        Assert.True(limiter.TryAcquire("user-1", "restart", "1", out _));

        _now = _now.AddSeconds(29);

        Assert.False(limiter.TryAcquire("user-1", "restart", "1", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Stopall_uses_sixty_second_cooldown()
    {
        var limiter = CreateLimiter();
        Assert.True(limiter.TryAcquire("user-1", "stopall", null, out _));

        _now = _now.AddSeconds(59);

        Assert.False(limiter.TryAcquire("user-1", "stopall", null, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Stopall_is_allowed_after_sixty_second_cooldown()
    {
        var limiter = CreateLimiter();
        Assert.True(limiter.TryAcquire("user-1", "stopall", null, out _));

        _now = _now.AddSeconds(60);

        Assert.True(limiter.TryAcquire("user-1", "stopall", null, out _));
    }

    [Fact]
    public void Stopall_confirmation_does_not_repeat_the_initial_rate_limit()
    {
        Assert.True(DiscordCommandRateLimiter.RequiresAcquire("stopall", []));
        Assert.False(DiscordCommandRateLimiter.RequiresAcquire("stopall", ["confirm"]));
        Assert.True(DiscordCommandRateLimiter.RequiresAcquire("restart", ["confirm"]));
    }

    [Fact]
    public void Concurrent_calls_for_same_key_allow_only_one_first_acquire()
    {
        var limiter = CreateLimiter();

        var results = Enumerable.Range(0, 100)
            .AsParallel()
            .Select(_ => limiter.TryAcquire("user-1", "status", "1", out var _))
            .ToArray();

        Assert.Equal(1, results.Count(result => result));
        Assert.Equal(99, results.Count(result => !result));
    }

    [Fact]
    public void Concurrent_calls_for_different_keys_do_not_throw()
    {
        var limiter = CreateLimiter();

        var exception = Record.Exception(() =>
        {
            _ = Enumerable.Range(0, 500)
                .AsParallel()
                .Select(index => limiter.TryAcquire($"user-{index % 25}", "status", (index % 10).ToString(), out _))
                .ToArray();
        });

        Assert.Null(exception);
    }

    private DiscordCommandRateLimiter CreateLimiter()
    {
        return new DiscordCommandRateLimiter(() => _now);
    }
}
