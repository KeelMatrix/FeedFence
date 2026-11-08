using NuGet.Configuration;

namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed class EffectiveConfig
{
    private readonly PackageSourceMapping _mapping;

    public EffectiveConfig(IReadOnlyList<SourceObservation> sources, PackageSourceMapping mapping, IReadOnlySet<string> mappedSourceNames)
    {
        Sources = sources;
        _mapping = mapping;
        MappedSourceNames = mappedSourceNames;
    }

    public IReadOnlyList<SourceObservation> Sources { get; }
    public bool MappingEnabled => _mapping.IsEnabled;
    public IReadOnlySet<string> MappedSourceNames { get; }

    public IReadOnlyList<string> GetEligibleSourceNames(string packageId)
    {
        var activeNames = Sources
            .Where(source => source.IsEnabled)
            .ToDictionary(source => source.Name, StringComparer.OrdinalIgnoreCase);
        if (!_mapping.IsEnabled)
        {
            return activeNames.Values.Select(source => source.Name).Order(StringComparer.Ordinal).ToArray();
        }

        var configuredNames = _mapping.GetConfiguredPackageSources(packageId);
        return configuredNames
            .Where(activeNames.ContainsKey)
            .Select(name => activeNames[name].Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
internal sealed record SourceObservation(string Name, string Source, bool IsEnabled, string ConfigPath, string Scope)
{
    public bool IsLocal => !Source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
