using System.Text;
using System.Text.Json;

namespace KeelMatrix.FeedFence;

internal static class ReportRenderer
{
    public static string Render(AnalysisResult result, ReportFormat format) => format switch
    {
        ReportFormat.Text => TextReport.Render(result),
        ReportFormat.Json => JsonReport.Render(result),
        ReportFormat.Sarif => SarifReport.Render(result),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}

internal static class JsonReport
{
    public const int SchemaVersion = 1;

    public static string Render(AnalysisResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("format", "json");
            writer.WriteStartObject("tool");
            writer.WriteString("name", "FeedFence");
            writer.WriteString("version", VersionInfo.Current);
            writer.WriteEndObject();
            writer.WriteNumber("exitCode", result.ExitCode);
            writer.WriteStartObject("summary");
            writer.WriteNumber("resolvedPackageCount", result.PackageCount);
            writer.WriteNumber("activeSourceCount", result.ActiveSourceCount);
            writer.WriteBoolean("packageSourceMappingEnabled", result.MappingEnabled);
            writer.WriteNumber("deterministicMappingCount", result.DeterministicMappingCount);
            writer.WriteEndObject();
            WriteSources(writer, result);
            WriteDiagnostics(writer, result);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    internal static void WriteSources(Utf8JsonWriter writer, AnalysisResult result)
    {
        writer.WriteStartArray("sources");
        foreach (var source in result.Sources.OrderBy(source => source.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("key", FeedFenceAnalyzer.RedactLabel(source.Key));
            writer.WriteBoolean("enabled", source.IsEnabled);
            writer.WriteString("provenance", source.Provenance);
            writer.WriteBoolean("repositoryControlled", source.IsRepositoryControlled);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    internal static void WriteDiagnostics(Utf8JsonWriter writer, AnalysisResult result)
    {
        writer.WriteStartArray("diagnostics");
        foreach (var diagnostic in result.Diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("severity", SeverityText(diagnostic.Severity));
            writer.WriteString("message", diagnostic.Message);
            if (diagnostic.PackageId is not null)
            {
                writer.WriteString("packageId", diagnostic.PackageId);
            }

            if (diagnostic.SourceKeys is { Count: > 0 })
            {
                writer.WriteStartArray("sourceKeys");
                foreach (var sourceKey in diagnostic.SourceKeys.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(FeedFenceAnalyzer.RedactLabel(sourceKey));
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    internal static string SeverityText(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Violation => "violation",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Information => "information",
        _ => "diagnostic"
    };
}

internal static class SarifReport
{
    private const string SchemaUri = "https://json.schemastore.org/sarif-2.1.0.json";

    public static string Render(AnalysisResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", SchemaUri);
            writer.WriteString("version", "2.1.0");
            writer.WriteStartArray("runs");
            writer.WriteStartObject();
            writer.WriteStartObject("tool");
            writer.WriteStartObject("driver");
            writer.WriteString("name", "FeedFence");
            writer.WriteString("version", VersionInfo.Current);
            writer.WriteString("informationUri", "https://github.com/KeelMatrix/FeedFence");
            writer.WriteStartArray("rules");
            foreach (var rule in DiagnosticRules.All)
            {
                writer.WriteStartObject();
                writer.WriteString("id", rule.Code);
                writer.WriteString("name", rule.Name);
                writer.WriteStartObject("shortDescription");
                writer.WriteString("text", rule.Description);
                writer.WriteEndObject();
                writer.WriteStartObject("defaultConfiguration");
                writer.WriteString("level", SarifLevel(rule.DefaultSeverity));
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartArray("results");
            foreach (var diagnostic in result.Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("ruleId", diagnostic.Code);
                writer.WriteString("level", SarifLevel(diagnostic.Severity));
                writer.WriteStartObject("message");
                writer.WriteString("text", diagnostic.Message);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static string SarifLevel(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Violation => "error",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Information => "note",
        _ => "warning"
    };
}

internal static class DiagnosticRules
{
    public static IReadOnlyList<DiagnosticRule> All { get; } =
    [
        new("FF001", "MultiSourceWithoutMapping", "Multiple active sources are available without Package Source Mapping.", DiagnosticSeverity.Violation),
        new("FF002", "AmbiguousWinningMapping", "Multiple sources remain eligible at the same winning mapping specificity.", DiagnosticSeverity.Violation),
        new("FF003", "UnmappedResolvedPackage", "A resolved package has no eligible active mapped source.", DiagnosticSeverity.Violation),
        new("FF004", "MappingSourceKeyMismatch", "A mapping source identity does not correspond to any configured source identity; case-only differences follow NuGet source-key matching.", DiagnosticSeverity.Violation),
        new("FF005", "InheritedActiveSource", "An active source originates outside repository-controlled configuration.", DiagnosticSeverity.Warning),
        new("FF006", "InsecureSource", "An applicable source uses plain HTTP or another insecure source form.", DiagnosticSeverity.Violation),
        new("FF007", "ProtectedPatternEscape", "A protected or private package can resolve outside its declared trust set.", DiagnosticSeverity.Violation),
        new("FF008", "MappingScopeLimitation", "Package Source Mapping does not constrain every NuGet metadata query.", DiagnosticSeverity.Information)
    ];
}

internal sealed record DiagnosticRule(
    string Code,
    string Name,
    string Description,
    DiagnosticSeverity DefaultSeverity);
