using System.Collections.Concurrent;
using System.Diagnostics;

namespace SupermarketErp.Api.Features.Auth;

public sealed class LoginRateLimiter
{
    private readonly int _ipCapacity;
    private readonly TimeSpan _ipWindow;
    private readonly int _usernameCapacity;
    private readonly TimeSpan _usernameWindow;
    private readonly int _maxBuckets;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private long _operations;

    public LoginRateLimiter(IConfiguration configuration)
    {
        _ipCapacity = Positive(configuration, "LOGIN_RATE_LIMIT_IP_CAPACITY", 30);
        _ipWindow = Seconds(configuration, "LOGIN_RATE_LIMIT_IP_WINDOW_SECONDS", 60);
        _usernameCapacity = Positive(configuration, "LOGIN_RATE_LIMIT_USERNAME_CAPACITY", 10);
        _usernameWindow = Seconds(configuration, "LOGIN_RATE_LIMIT_USERNAME_WINDOW_SECONDS", 60);
        _maxBuckets = Positive(configuration, "LOGIN_RATE_LIMIT_MAX_BUCKETS", 10_000);
    }

    public void Check(string? clientIp, string? username)
    {
        var ipRetry = GetBucket("ip:" + Normalize(clientIp), _ipCapacity, _ipWindow).TryConsume();
        var usernameRetry = GetBucket("username:" + Normalize(username), _usernameCapacity, _usernameWindow).TryConsume();
        var retryAfter = Math.Max(ipRetry, usernameRetry);
        if (retryAfter > 0) throw new LoginRateLimitExceededException(retryAfter);

        if (Interlocked.Increment(ref _operations) % 500 == 0 && _buckets.Count > _maxBuckets)
        {
            var remove = _buckets.Count - _maxBuckets;
            foreach (var (key, _) in _buckets.OrderBy(pair => pair.Value.LastTouched).Take(remove))
                _buckets.TryRemove(key, out _);
        }
    }

    private Bucket GetBucket(string key, int capacity, TimeSpan window) =>
        _buckets.GetOrAdd(key, _ => new Bucket(capacity, window));

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant();

    private static int Positive(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var value) && value > 0
            ? value
            : configuration[key] is null ? fallback : throw new InvalidOperationException($"{key} must be positive.");

    private static TimeSpan Seconds(IConfiguration configuration, string key, int fallback)
    {
        var seconds = Positive(configuration, key, fallback);
        return TimeSpan.FromSeconds(seconds);
    }

    private sealed class Bucket(int capacity, TimeSpan window)
    {
        private readonly double _windowTicks = window.TotalSeconds * Stopwatch.Frequency;
        private double _tokens = capacity;
        private long _lastRefill = Stopwatch.GetTimestamp();
        private long _lastTouched = Stopwatch.GetTimestamp();

        public long LastTouched => Interlocked.Read(ref _lastTouched);

        public long TryConsume()
        {
            lock (this)
            {
                var now = Stopwatch.GetTimestamp();
                _tokens = Math.Min(capacity, _tokens + Math.Max(0, now - _lastRefill) / _windowTicks * capacity);
                _lastRefill = now;
                Interlocked.Exchange(ref _lastTouched, now);
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return 0;
                }

                return Math.Max(1, (long)Math.Ceiling((1 - _tokens) / capacity * window.TotalSeconds));
            }
        }
    }
}

public sealed class LoginRateLimitExceededException(long retryAfterSeconds) : Exception("Too many login attempts. Try again later.")
{
    public long RetryAfterSeconds { get; } = Math.Max(1, retryAfterSeconds);
}
