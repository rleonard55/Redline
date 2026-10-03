using Redline.CompatibilityHarness.Probes;
using Redline.Core.Models;

namespace Redline.CompatibilityHarness.Report;

public record MachineInfo
{
    public string OSVersion { get; init; } = Environment.OSVersion.ToString();
    public int ProcessorCount { get; init; } = Environment.ProcessorCount;
    public double PrimaryDpiScale { get; set; } = 1.0;
    public string ClrVersion { get; init; } = Environment.Version.ToString();
}

public record AppCompatibilityResult
{
    public required string AppName { get; init; }
    public required TargetAppInfo TargetInfo { get; init; }
    public required PatternSupportResult PatternSupport { get; init; }
    public required TextReadResult TextRead { get; init; }
    public CaretSelectionResult? CaretSelection { get; init; }
    public GeometryResult? Geometry { get; init; }
    public ChangeDetectionResult? ChangeDetection { get; init; }
    public ReplaceResult? Replace { get; init; }
}

public class CompatibilityReport
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public MachineInfo Machine { get; set; } = new();
    public List<AppCompatibilityResult> Results { get; set; } = new();
}
