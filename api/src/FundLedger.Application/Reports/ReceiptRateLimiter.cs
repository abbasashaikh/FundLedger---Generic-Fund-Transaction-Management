using System.Collections.Concurrent;
using FundLedger.Application.Errors;

namespace FundLedger.Application.Reports;

/// <summary>TR-068: at most 60 receipts per user per hour (sliding window, kept in memory per API instance).</summary>
public sealed class ReceiptRateLimiter
{
    public const int PerHour = 60;

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _hits = new();

    public void Take(Guid userId, DateTimeOffset now)
    {
        var hits = _hits.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (hits)
        {
            while (hits.Count > 0 && now - hits.Peek() >= TimeSpan.FromHours(1))
            {
                hits.Dequeue();
            }

            if (hits.Count >= PerHour)
            {
                throw new RateLimitedException("You've generated a lot of receipts. Please try again in a little while.");
            }

            hits.Enqueue(now);
        }
    }
}
