using System.Text.Json;
using NuGet.Common;
using NuGet.Packaging;

namespace KeelMatrix.FeedFence;

internal static class EvaluatedRestoreReader
{
    public static EvaluatedRestoreEvidence Read(string projectPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", Path.GetFileName(projectPath) + ".nuget.dgspec.json");
        if (!File.Exists(path))
        {
            throw new AnalysisException("evaluated restore input evidence (.nuget.dgspec.json) is missing; run restore first, then run FeedFence again.");
        }
        try
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024)
            {
                throw new AnalysisException("evaluated restore input evidence exceeds the supported input size.");
            }
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format) || !format.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("restore", out var restore) || restore.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("evaluated restore input evidence is incomplete; restore again before analysis.");
            }
            var comparer = PathUtility.GetStringComparerBasedOnOS();
            var projectMap = new Dictionary<string, JsonElement>(comparer);
            foreach (var project in projects.EnumerateObject())
            {
                if (projectMap.Count >= 4096 || !projectMap.TryAdd(Path.GetFullPath(project.Name), project.Value.Clone()))
                {
                    throw new AnalysisException("evaluated restore input evidence contains too many or duplicate projects.");
                }
            }
            var fullPath = Path.GetFullPath(projectPath);
            var physicalPath = Provenance.ResolvePhysicalPath(fullPath);
            var evidencePath = projectMap.Keys.FirstOrDefault(key => comparer.Equals(key, fullPath) ||
                (physicalPath is not null && comparer.Equals(Provenance.ResolvePhysicalPath(key), physicalPath)));
            if (evidencePath is null || !restore.EnumerateObject().Any(item => comparer.Equals(Path.GetFullPath(item.Name), evidencePath)))
            {
                throw new AnalysisException("evaluated restore input evidence does not identify the supplied project; restore again before analysis.");
            }
            return new(evidencePath, projectMap);
        }
        catch (AnalysisException)
        {
            throw;
        }
        catch
        {
            throw new AnalysisException("evaluated restore input evidence is malformed; restore again before analysis.");
        }
    }
}

internal sealed record EvaluatedRestoreEvidence(string ProjectPath, IReadOnlyDictionary<string, JsonElement> Projects);

internal static class InputIdentity
{
    public static string RequirePackageId(string value, string message)
    {
        if (!PackageIdValidator.IsValidPackageId(value, useRestrictedCharacterSet: false))
        {
            throw new AnalysisException(message);
        }

        return value;
    }
}
