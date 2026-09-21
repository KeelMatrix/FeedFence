using System.Text.Json;
using System.Text.Json.Serialization;
using KeelMatrix.Telemetry;

namespace KeelMatrix.FeedFence;

internal static class FeedFenceTelemetry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void TrackActivation(AnalysisResult result)
    {
        if (CreatePayload(result) is null)
        {
            return;
        }

        try
        {
            new Client("feedfence", typeof(VersionInfo)).TrackActivation();
        }
        catch
        {
            // Telemetry is best effort and must never change analysis behavior.
        }
    }

    internal static FeedFenceTelemetryPayload? CreatePayload(AnalysisResult result)
    {
        if (result.PackageCount < 1 || !result.EffectiveSourcePolicyEvaluated)
        {
            return null;
        }

        return new(
            VersionInfo.Current,
            Environment.Version.Major,
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other",
            Bucket(result.PackageCount),
            Bucket(result.ActiveSourceCount),
            result.MappingEnabled,
            result.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Violation)
                ? "violation"
                : result.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
                    ? "warning"
                    : "pass",
            Bucket(result.Diagnostics.Count));
    }

    internal static string SerializePayload(AnalysisResult result)
    {
        var payload = CreatePayload(result) ?? throw new InvalidOperationException("telemetry activation is not eligible for this result.");
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string Bucket(int count) => count switch
    {
        <= 0 => "0",
        <= 5 => "1-5",
        <= 20 => "6-20",
        _ => "21+"
    };
}

internal sealed record FeedFenceTelemetryPayload(
    string FeedFenceVersion,
    [property: JsonPropertyName("dotnetMajorVersion")]
    int DotNetMajorVersion,
    string OsFamily,
    string ResolvedPackageCountBucket,
    string ActiveSourceCountBucket,
    bool PackageSourceMappingEnabled,
    string ResultClass,
    string DiagnosticCountBucket);
