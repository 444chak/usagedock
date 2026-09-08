using System.Net;
using System.Net.Http.Headers;
using UsageDock.Services;
using Xunit;

namespace UsageDock.Tests;

public class RateLimitPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(6, 960)]
    [InlineData(7, 1800)]
    [InlineData(20, 1800)]
    public void Backoff_doubles_then_caps(int failures, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RateLimitPolicy.ComputeBackoff(failures));

    [Fact]
    public void First_attempt_is_always_allowed() =>
        Assert.True(new RateLimitPolicy().CanAttempt(Now));

    [Fact]
    public void A_failure_blocks_attempts_until_the_backoff_elapses()
    {
        var policy = new RateLimitPolicy();
        var delay = policy.RecordFailure(Now);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
        Assert.False(policy.CanAttempt(Now));
        Assert.False(policy.CanAttempt(Now.AddSeconds(29)));
        Assert.True(policy.CanAttempt(Now.AddSeconds(30)));
    }

    [Fact]
    public void Success_clears_the_backoff()
    {
        var policy = new RateLimitPolicy();
        policy.RecordFailure(Now);
        policy.RecordFailure(Now);
        Assert.Equal(2, policy.ConsecutiveFailures);

        policy.RecordSuccess();

        Assert.Equal(0, policy.ConsecutiveFailures);
        Assert.True(policy.CanAttempt(Now));
    }

    [Fact]
    public void Retry_after_wins_when_it_is_longer_than_the_backoff()
    {
        var policy = new RateLimitPolicy();

        var delay = policy.RecordFailure(Now, TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(5), delay);
        Assert.False(policy.CanAttempt(Now.AddMinutes(4)));
        Assert.True(policy.CanAttempt(Now.AddMinutes(5)));
    }

    [Fact]
    public void Retry_after_is_ignored_when_shorter_than_the_backoff()
    {
        var policy = new RateLimitPolicy();
        policy.RecordFailure(Now);
        policy.RecordFailure(Now);

        // Third failure would be 120s on its own.
        var delay = policy.RecordFailure(Now, TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(120), delay);
    }

    [Fact]
    public void Retry_after_is_capped_at_the_maximum_backoff()
    {
        var policy = new RateLimitPolicy();

        var delay = policy.RecordFailure(Now, TimeSpan.FromHours(6));

        Assert.Equal(RateLimitPolicy.MaxBackoff, delay);
    }

    [Theory]
    [InlineData("120", 120)]
    [InlineData("0", 0)]
    [InlineData("-5", 0)]
    public void Retry_after_parses_delta_seconds(string header, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RateLimitPolicy.ParseRetryAfter(header, Now));

    [Fact]
    public void Retry_after_parses_an_http_date()
    {
        var value = Now.AddMinutes(3).ToString("R");

        var parsed = RateLimitPolicy.ParseRetryAfter(value, Now);

        Assert.NotNull(parsed);
        Assert.Equal(180, Math.Round(parsed!.Value.TotalSeconds));
    }

    [Fact]
    public void Retry_after_in_the_past_becomes_zero()
    {
        var value = Now.AddMinutes(-3).ToString("R");

        Assert.Equal(TimeSpan.Zero, RateLimitPolicy.ParseRetryAfter(value, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    public void Unusable_retry_after_values_return_null(string? header) =>
        Assert.Null(RateLimitPolicy.ParseRetryAfter(header, Now));

    [Fact]
    public void Retry_after_is_read_off_a_response()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));

        Assert.Equal(TimeSpan.FromSeconds(45), RateLimitPolicy.ParseRetryAfter(response, Now));
    }

    [Fact]
    public void A_response_without_the_header_returns_null()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        Assert.Null(RateLimitPolicy.ParseRetryAfter(response, Now));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void Hard_auth_failures_are_recognised(HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, RateLimitPolicy.IsHardAuthFailure(status));
}
