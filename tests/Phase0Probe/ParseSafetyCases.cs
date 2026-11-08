using System.Text.Json;
using NuGet.Configuration;

namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed partial class ProbeRunner
{
    private void RunParseSafetyCases(string runRoot)
    {
        var malformedConfigPath = Path.Combine(runRoot, "parse-safety", "malformed.config");
        var malformedConfigRejected = TryLoadConfigAndGetFailure(malformedConfigPath, out var malformedConfigFailure);
        Assert("malformed NuGet config is rejected", malformedConfigRejected);
        Console.WriteLine($"Parse fixture: malformed NuGet.Config; rejected={(malformedConfigRejected ? "yes" : "no")}; exception={malformedConfigFailure ?? "none"}");

        var dtdConfigPath = Path.Combine(runRoot, "parse-safety", "dtd.config");
        var dtdRejected = TryLoadConfigAndGetFailure(dtdConfigPath, out var dtdFailure);
        Assert("DTD/external-entity NuGet config is rejected", dtdRejected);
        Console.WriteLine($"Parse fixture: DTD/external entity NuGet.Config; rejected={(dtdRejected ? "yes" : "no")}; exception={dtdFailure ?? "none"}");

        var malformedJsonPath = Path.Combine(runRoot, "parse-safety", "malformed.json");
        var malformedJsonRejected = TryParseJsonAndGetFailure(malformedJsonPath, out var malformedJsonFailure);
        Assert("malformed JSON is rejected", malformedJsonRejected);
        Console.WriteLine($"Parse fixture: malformed JSON; rejected={(malformedJsonRejected ? "yes" : "no")}; exception={malformedJsonFailure ?? "none"}");
    }

    private static bool TryLoadConfigAndGetFailure(string path, out string? exceptionType)
    {
        try
        {
            Settings.LoadSettingsGivenConfigPaths([path]);
            exceptionType = null;
            return false;
        }
        catch (Exception exception)
        {
            exceptionType = exception.GetType().Name;
            return true;
        }
    }

    private static bool TryParseJsonAndGetFailure(string path, out string? exceptionType)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            document.RootElement.ToString();
            exceptionType = null;
            return false;
        }
        catch (Exception exception)
        {
            exceptionType = exception.GetType().Name;
            return true;
        }
    }
}
