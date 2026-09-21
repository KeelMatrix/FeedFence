namespace KeelMatrix.FeedFence;

internal sealed class TextReport
{
    public static string Render(AnalysisResult result)
    {
        var lines = new List<string>
        {
            $"FeedFence: {result.PackageCount} resolved packages, {result.ActiveSourceCount} active sources, " +
            (result.MappingEnabled
                ? $"{result.DeterministicMappingCount} deterministic mappings."
                : "Package Source Mapping disabled."),
            "Effective sources:"
        };

        foreach (var source in result.Sources.OrderBy(source => source.Key, StringComparer.Ordinal))
        {
            lines.Add($"- \"{FeedFenceAnalyzer.RedactLabel(source.Key)}\" ({(source.IsEnabled ? "active" : "disabled")}; {source.Provenance})");
        }

        if (result.Diagnostics.Count > 0)
        {
            lines.Add("Diagnostics:");
            foreach (var diagnostic in result.Diagnostics)
            {
                lines.Add($"{diagnostic.Code} [{SeverityText(diagnostic.Severity)}]: {diagnostic.Message}");
            }
        }

        var violationCount = result.Diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Violation);
        if (violationCount == 0)
        {
            lines.Add("No restore-source policy violations found.");
        }
        else
        {
            lines.Add($"Policy violations: {violationCount}.");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string SeverityText(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Violation => "violation",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Information => "information",
        _ => "diagnostic"
    };
}
