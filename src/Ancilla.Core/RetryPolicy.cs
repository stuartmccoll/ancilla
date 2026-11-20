using System.Net;

namespace Ancilla.Core;

/// <summary>
/// Backs off and tries again when the service answers 429 (too many requests): the wait doubles
/// each time, from <see cref="BaseDelay"/>. Any other failure is passed on straight away.
/// </summary>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan BaseDelay)
{
    public static RetryPolicy Default { get; } = new(5, TimeSpan.FromSeconds(2));

    /// <summary>No retries, for tests that want the failure.</summary>
    public static RetryPolicy None { get; } = new(1, TimeSpan.Zero);

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await operation(); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromTicks(BaseDelay.Ticks << (attempt - 1)), ct);
            }
        }
    }
}
