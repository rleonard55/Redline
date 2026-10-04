using System.Collections.Concurrent;

namespace Redline.Core.Diagnostics;

/// <summary>Timing summary over a counter's most recent samples (<see cref="PerfCounters.Window"/>).</summary>
public sealed record PerfStat(string Name, long Count, double MeanMs, double P50Ms, double P95Ms, double MaxMs)
{
    public override string ToString() => $"{Name} {P50Ms:F0}/{P95Ms:F0}/{MaxMs:F0} ms (n={Count})";
}

/// <summary>
/// Named latency counters (analysis, text reads, overlay layout, corrections). Thread-safe and cheap
/// enough to record on every operation; stats cover the last <see cref="Window"/> samples per counter.
/// </summary>
public sealed class PerfCounters
{
    public const int Window = 256;

    private readonly ConcurrentDictionary<string, Series> _series = new(StringComparer.Ordinal);

    public void Record(string name, TimeSpan elapsed) =>
        _series.GetOrAdd(name, _ => new Series()).Add(elapsed.TotalMilliseconds);

    /// <summary>Counters with at least one sample, ordered by name.</summary>
    public IReadOnlyList<PerfStat> Snapshot() =>
        _series.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value.Stat(kv.Key))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

    /// <summary>One line: "name p50/p95/max ms (n=…)" per counter, or an empty string.</summary>
    public string Summary() => string.Join("; ", Snapshot());

    private sealed class Series
    {
        private readonly double[] _samples = new double[Window];
        private long _count;

        public void Add(double ms)
        {
            lock (_samples)
            {
                _samples[_count % Window] = ms;
                _count++;
            }
        }

        public PerfStat? Stat(string name)
        {
            double[] recent;
            long count;
            lock (_samples)
            {
                count = _count;
                if (count == 0) return null;
                recent = _samples.Take((int)Math.Min(count, Window)).ToArray();
            }

            Array.Sort(recent);
            return new PerfStat(name, count, recent.Average(), Percentile(recent, 0.50), Percentile(recent, 0.95), recent[^1]);
        }

        /// <summary>Nearest-rank percentile of sorted samples.</summary>
        private static double Percentile(double[] sorted, double p)
        {
            int rank = (int)Math.Ceiling(p * sorted.Length);
            return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
        }
    }
}
