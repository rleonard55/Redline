using System.Diagnostics;

namespace Redline.Core.Diagnostics;

/// <summary>Redline's own memory and handle use (for the perf summary and the Diagnostics window).</summary>
public readonly record struct ResourceUsage(long PrivateBytes, long WorkingSetBytes, long ManagedHeapBytes, int Handles, int Threads)
{
    public static ResourceUsage Current()
    {
        using var p = Process.GetCurrentProcess();
        return new ResourceUsage(p.PrivateMemorySize64, p.WorkingSet64, GC.GetTotalMemory(false), p.HandleCount, p.Threads.Count);
    }

    public override string ToString() => FormattableString.Invariant(
        $"{Mb(PrivateBytes)} MB private, {Mb(WorkingSetBytes)} MB working set, {Mb(ManagedHeapBytes)} MB managed, {Handles} handles, {Threads} threads");

    private static long Mb(long bytes) => (bytes + 512 * 1024) / (1024 * 1024);
}
