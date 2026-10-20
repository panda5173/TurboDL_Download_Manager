using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TurboDL.Core;

/// <summary>Global token-bucket speed limiter shared by all connections. 0 = unlimited (zero overhead).</summary>
public sealed class RateLimiter
{
    readonly object _lock = new();
    long _bytesPerSecond;
    double _tokens;
    long _lastTimestamp = Stopwatch.GetTimestamp();

    public long BytesPerSecond => Volatile.Read(ref _bytesPerSecond);
    public bool Enabled => BytesPerSecond > 0;

    public void SetLimit(long bytesPerSecond)
    {
        lock (_lock)
        {
            Volatile.Write(ref _bytesPerSecond, Math.Max(0, bytesPerSecond));
            _tokens = 0;
            _lastTimestamp = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Call after receiving <paramref name="bytes"/>; waits if the global budget is overdrawn.</summary>
    public ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        long bps = BytesPerSecond;
        if (bps <= 0) return ValueTask.CompletedTask;

        int delayMs;
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            _lastTimestamp = now;
            _tokens = Math.Min(_tokens + elapsed * bps, bps * 0.5); // allow max 0.5 s burst
            _tokens -= bytes;
            if (_tokens >= 0) return ValueTask.CompletedTask;
            delayMs = (int)Math.Ceiling(-_tokens * 1000.0 / bps);
        }
        return new ValueTask(Task.Delay(Math.Clamp(delayMs, 1, 2000), ct));
    }
}
