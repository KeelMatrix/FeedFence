using System.Text.Json;

namespace KeelMatrix.FeedFence;

internal sealed class FeedFencePolicy
{
    private const int MaxBytes = 256 * 1024;
    private const int MaxEntries = 512;
    private readonly IReadOnlyDictionary<string, string> _sourceTrust;
    private readonly IReadOnlyList<PolicyException> _exceptions;

    private FeedFencePolicy(
        IReadOnlyDictionary<string, string> sourceTrust,
        IReadOnlyList<PackageRule> packageRules,
        IReadOnlyList<PolicyException> exceptions)
    {
        _sourceTrust = sourceTrust;
        PackageRules = packageRules;
        _exceptions = exceptions;
    }

    public static FeedFencePolicy Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        [],
        []);

    public IReadOnlyList<PackageRule> PackageRules { get; }

    public static FeedFencePolicy Load(string repositoryRoot, string? explicitPath)
    {
        var path = explicitPath is null ? Path.Combine(repositoryRoot, "feedfence.json") : GetFullPath(explicitPath);
        if (!File.Exists(path))
        {
            if (explicitPath is null)
            {
                return Empty;
            }

            throw new AnalysisException("the supplied FeedFence policy could not be read.");
        }

        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
        {
            throw new AnalysisException("the FeedFence policy exceeds the supported input size.");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            return Parse(document.RootElement);
        }
        catch (AnalysisException)
        {
            throw;
        }
        catch
        {
            throw new AnalysisException("the FeedFence policy is malformed or unreadable.");
        }
    }

    public bool IsTrusted(string sourceKey)
    {
        return _sourceTrust.TryGetValue(sourceKey, out var trust) &&
            trust is "private" or "trusted" or "internal" or "repository";
    }

    public bool IsExcepted(Diagnostic diagnostic)
    {
        return _exceptions.Any(exception => exception.Applies(diagnostic));
    }

    private static FeedFencePolicy Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AnalysisException("the FeedFence policy root must be a JSON object.");
        }

        if (root.TryGetProperty("version", out var version) &&
            (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != 1))
        {
            throw new AnalysisException("the FeedFence policy schema version is unsupported.");
        }

        var sourceTrust = ParseSourceTrust(root);
        var packageRules = new List<PackageRule>();
        ParsePackageRules(root, "protectedPackages", isPrivate: false, packageRules);
        ParsePackageRules(root, "privatePackages", isPrivate: true, packageRules);
        var exceptions = ParseExceptions(root);
        return new(sourceTrust, packageRules, exceptions);
    }

    private static Dictionary<string, string> ParseSourceTrust(JsonElement root)
    {
        var propertyName = root.TryGetProperty("sourceTrust", out _) ? "sourceTrust" : "sources";
        if (!root.TryGetProperty(propertyName, out var sources))
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        if (sources.ValueKind != JsonValueKind.Object || sources.EnumerateObject().Count() > MaxEntries)
        {
            throw new AnalysisException("the FeedFence policy source trust labels are invalid.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.EnumerateObject())
        {
            var label = source.Value.ValueKind == JsonValueKind.String
                ? source.Value.GetString()
                : source.Value.ValueKind == JsonValueKind.Object && source.Value.TryGetProperty("trust", out var trust)
                    ? trust.GetString()
                    : source.Value.ValueKind == JsonValueKind.Object && source.Value.TryGetProperty("label", out var labelProperty)
                        ? labelProperty.GetString()
                        : null;
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new AnalysisException("every FeedFence source trust label must be a non-empty string.");
            }

            result[source.Name] = label.Trim().ToLowerInvariant();
        }

        return result;
    }

    private static void ParsePackageRules(JsonElement root, string propertyName, bool isPrivate, List<PackageRule> destination)
    {
        if (!root.TryGetProperty(propertyName, out var rules))
        {
            return;
        }

        if (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() > MaxEntries)
        {
            throw new AnalysisException($"FeedFence policy property '{propertyName}' must be a bounded array.");
        }

        foreach (var element in rules.EnumerateArray())
        {
            string? pattern;
            IReadOnlyList<string> allowedSources;
            if (element.ValueKind == JsonValueKind.String)
            {
                pattern = element.GetString();
                allowedSources = [];
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                pattern = ReadString(element, "pattern") ?? ReadString(element, "packagePattern");
                allowedSources = ReadStringArray(element, "allowedSources").Cast<string>().ToArray();
            }
            else
            {
                throw new AnalysisException($"FeedFence policy property '{propertyName}' contains an invalid rule.");
            }

            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new AnalysisException($"FeedFence policy property '{propertyName}' contains a rule without a pattern.");
            }

            PatternMatcher.Validate(pattern);
            destination.Add(new(pattern, isPrivate, allowedSources));
        }
    }

    private static List<PolicyException> ParseExceptions(JsonElement root)
    {
        if (!root.TryGetProperty("exceptions", out var exceptions))
        {
            return [];
        }

        if (exceptions.ValueKind != JsonValueKind.Array || exceptions.GetArrayLength() > MaxEntries)
        {
            throw new AnalysisException("FeedFence policy exceptions must be a bounded array.");
        }

        var result = new List<PolicyException>();
        foreach (var element in exceptions.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new AnalysisException("every FeedFence policy exception must be an object.");
            }

            var code = ReadString(element, "code");
            var reason = ReadString(element, "reason");
            var packagePattern = ReadSelector(element, "packagePattern", "pattern", "package pattern");
            var sourceKey = ReadSelector(element, "sourceKey", "source", "source key");
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(reason) ||
                (string.IsNullOrWhiteSpace(packagePattern) && string.IsNullOrWhiteSpace(sourceKey)))
            {
                throw new AnalysisException("every FeedFence policy exception must identify a diagnostic, target, and reason.");
            }

            if (code is not ("FF001" or "FF002" or "FF003" or "FF004" or "FF005" or "FF006" or "FF007"))
            {
                throw new AnalysisException("a FeedFence policy exception names an unsupported diagnostic.");
            }

            if (packagePattern is not null)
            {
                PatternMatcher.Validate(packagePattern);
            }

            result.Add(new(code, packagePattern, sourceKey, reason));
        }

        return result;
    }

    private static string? ReadString(JsonElement objectElement, string propertyName)
    {
        if (!objectElement.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new AnalysisException($"FeedFence policy property '{propertyName}' must be a string.");
        }

        return property.GetString();
    }

    private static string? ReadSelector(JsonElement objectElement, string primaryName, string aliasName, string label)
    {
        var hasPrimary = objectElement.TryGetProperty(primaryName, out _);
        var hasAlias = objectElement.TryGetProperty(aliasName, out _);
        if (hasPrimary && hasAlias)
        {
            throw new AnalysisException($"FeedFence policy exception contains conflicting {label} selectors.");
        }

        return hasPrimary
            ? ReadString(objectElement, primaryName)
            : hasAlias
                ? ReadString(objectElement, aliasName)
                : null;
    }

    private static string?[] ReadStringArray(JsonElement objectElement, string propertyName)
    {
        if (!objectElement.TryGetProperty(propertyName, out var property))
        {
            return [];
        }

        if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > MaxEntries)
        {
            throw new AnalysisException($"FeedFence policy property '{propertyName}' must be a bounded string array.");
        }

        var result = property.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null).ToArray();
        if (result.Any(string.IsNullOrWhiteSpace))
        {
            throw new AnalysisException($"FeedFence policy property '{propertyName}' must contain only non-empty strings.");
        }

        return result.Cast<string>().ToArray();
    }

    private static string GetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            throw new AnalysisException("the supplied FeedFence policy path is invalid.");
        }
    }
}

internal sealed record PackageRule(string Pattern, bool IsPrivate, IReadOnlyList<string> AllowedSources);

internal sealed record PolicyException(string Code, string? PackagePattern, string? SourceKey, string Reason)
{
    public bool Applies(Diagnostic diagnostic)
    {
        if (!string.Equals(Code, diagnostic.Code, StringComparison.Ordinal))
        {
            return false;
        }

        if (PackagePattern is not null && (diagnostic.PackageId is null || !PatternMatcher.Matches(PackagePattern, diagnostic.PackageId)))
        {
            return false;
        }

        return SourceKey is null ||
            (diagnostic.SourceKeys is { Count: > 0 } sourceKeys &&
             sourceKeys.All(key => string.Equals(key, SourceKey, StringComparison.OrdinalIgnoreCase)));
    }
}
