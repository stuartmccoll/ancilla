using System.Net;

namespace Ancilla.Core.Tests;

public sealed class RetryPolicyTests
{
    private static HttpRequestException Failure(HttpStatusCode status) => new("failed", null, status);

    [Fact]
    public async Task A_busy_answer_is_retried_until_it_works()
    {
        var calls = 0;
        var policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(1));
        var result = await policy.RunAsync(() => ++calls < 3 ? throw Failure(HttpStatusCode.TooManyRequests) : Task.FromResult("ok"));
        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task It_gives_up_after_the_last_attempt()
    {
        var calls = 0;
        var policy = new RetryPolicy(3, TimeSpan.FromMilliseconds(1));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => policy.RunAsync<string>(() => { calls++; throw Failure(HttpStatusCode.TooManyRequests); }));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Other_failures_are_not_retried(HttpStatusCode status)
    {
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => RetryPolicy.Default.RunAsync<string>(() => { calls++; throw Failure(status); }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task The_wait_doubles_each_time_and_can_be_cancelled()
    {
        var policy = new RetryPolicy(10, TimeSpan.FromHours(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.RunAsync<string>(() => throw Failure(HttpStatusCode.TooManyRequests), cts.Token));
    }
}
