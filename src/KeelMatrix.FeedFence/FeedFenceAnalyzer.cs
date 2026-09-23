using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using NuGet.Configuration;

namespace KeelMatrix.FeedFence;

internal sealed class FeedFenceAnalyzer
{
    public static AnalysisResult Analyze(CliOptions options)
    {
        var target = TargetResolver.Resolve(options.TargetPath);
        var configuration = EffectiveConfig.Load(target.RestoreContext, target.RepositoryRoot, options.ConfigPath);
        var policy = FeedFencePolicy.Load(target.RepositoryRoot, options.PolicyPath);
        var packageIds = RestoreGraphReader.ReadFromProjects(target.ProjectPaths);
        if (packageIds.Count > 0 && configuration.ActiveSources.Count == 0)
        {
            throw new AnalysisException("the restored package graph is non-empty but the effective NuGet configuration has no active package sources; restore artifacts are incomplete.");
        }

        var diagnostics = new List<Diagnostic>();

        foreach (var source in configuration.Sources.Where(source => source.IsEnabled))
        {
            if (!source.IsRepositoryControlled)
            {
                AddDiagnostic(
                    diagnostics,
                    policy,
                    new Diagnostic(
                        "FF005",
                        options.Strict ? DiagnosticSeverity.Violation : DiagnosticSeverity.Warning,
                        $"Active source key \"{RedactLabel(source.Key)}\" is inherited from {source.Provenance}; configuration provenance is not repository-controlled.",
                        SourceKeys: [source.Key]));
            }

            if (IsInsecureHttpSource(source.Value))
            {
                AddDiagnostic(
                    diagnostics,
                    policy,
                    new Diagnostic(
                        "FF006",
                        DiagnosticSeverity.Violation,
                        $"Source key \"{RedactLabel(source.Key)}\" uses an insecure HTTP source; use HTTPS or a documented local exception.",
                        SourceKeys: [source.Key]));
            }
        }

        if (!configuration.MappingEnabled && configuration.ActiveSources.Count > 1)
        {
            AddDiagnostic(
                diagnostics,
                policy,
                new Diagnostic(
                    "FF001",
                    DiagnosticSeverity.Violation,
                    $"Multiple active sources are available without Package Source Mapping: {FormatSourceKeys(configuration.ActiveSources)}.",
                    SourceKeys: configuration.ActiveSources.Select(source => source.Key).ToArray()));
        }

        foreach (var mapping in configuration.Mappings)
        {
            var configuredSource = configuration.Sources.FirstOrDefault(source =>
                string.Equals(source.Key, mapping.SourceKey, StringComparison.OrdinalIgnoreCase));
            if (configuredSource is null)
            {
                AddDiagnostic(
                    diagnostics,
                    policy,
                    new Diagnostic(
                        "FF004",
                        DiagnosticSeverity.Violation,
                        $"Mapping source key \"{RedactLabel(mapping.SourceKey)}\" does not correspond to a configured source key.",
                        SourceKeys: [mapping.SourceKey]));
            }
        }

        var deterministicMappings = 0;
        var effectiveSourcePolicyEvaluated = false;
        foreach (var packageId in packageIds)
        {
            var selection = configuration.Select(packageId);
            effectiveSourcePolicyEvaluated = true;
            if (configuration.MappingEnabled)
            {
                if (selection.Sources.Count == 0)
                {
                    AddDiagnostic(
                        diagnostics,
                        policy,
                        new Diagnostic(
                            "FF003",
                            DiagnosticSeverity.Violation,
                            $"Resolved package \"{packageId}\" has no eligible active source under Package Source Mapping.",
                            packageId));
                }
                else if (selection.Sources.Count == 1)
                {
                    deterministicMappings++;
                }
                else
                {
                    AddDiagnostic(
                        diagnostics,
                        policy,
                        new Diagnostic(
                            "FF002",
                            DiagnosticSeverity.Violation,
                            $"Resolved package \"{packageId}\" is eligible from {FormatSourceKeys(selection.Sources)} at the same winning mapping specificity ({FormatPatterns(selection.WinningPatterns)}).",
                            packageId,
                            selection.Sources.Select(source => source.Key).ToArray()));
                }

                AddProtectedPackageDiagnostics(diagnostics, policy, packageId, selection);
            }
            else
            {
                AddProtectedPackageDiagnostics(diagnostics, policy, packageId, selection);
            }
        }

        if (configuration.MappingEnabled)
        {
            AddDiagnostic(
                diagnostics,
                policy,
                new Diagnostic(
                    "FF008",
                    DiagnosticSeverity.Information,
                    "Package Source Mapping constrains package downloads; it does not constrain every NuGet metadata query."));
        }

        var ordered = diagnostics
            .OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.PackageId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray();
        var exitCode = ordered.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Violation) ? 1 : 0;
        return new(
            exitCode,
            packageIds.Count,
            configuration.ActiveSources.Count,
            configuration.MappingEnabled,
            deterministicMappings,
            effectiveSourcePolicyEvaluated,
            configuration.Sources,
            ordered);
    }

    private static void AddProtectedPackageDiagnostics(
        ICollection<Diagnostic> diagnostics,
        FeedFencePolicy policy,
        string packageId,
        MappingSelection selection)
    {
        foreach (var rule in policy.PackageRules.Where(rule => PatternMatcher.Matches(rule.Pattern, packageId)))
        {
            var outsideAllowedSources = rule.AllowedSources.Count > 0
                ? selection.Sources.Where(source => !rule.AllowedSources.Contains(source.Key, StringComparer.OrdinalIgnoreCase)).ToArray()
                : selection.Sources.Where(source => !policy.IsTrusted(source.Key)).ToArray();
            if (outsideAllowedSources.Length == 0)
            {
                continue;
            }

            var kind = rule.IsPrivate ? "private" : "protected";
            foreach (var source in outsideAllowedSources)
            {
                AddDiagnostic(
                    diagnostics,
                    policy,
                    new Diagnostic(
                        "FF007",
                        DiagnosticSeverity.Violation,
                        $"{kind} package \"{packageId}\" can resolve from source key \"{RedactLabel(source.Key)}\" outside its declared trust set (pattern \"{rule.Pattern}\").",
                        packageId,
                        [source.Key]));
            }
        }
    }

    private static void AddDiagnostic(ICollection<Diagnostic> diagnostics, FeedFencePolicy policy, Diagnostic diagnostic)
    {
        if (!string.Equals(diagnostic.Code, "FF008", StringComparison.Ordinal) && policy.IsExcepted(diagnostic))
        {
            return;
        }

        if (!diagnostics.Any(existing => existing.Code == diagnostic.Code &&
            existing.PackageId == diagnostic.PackageId &&
            existing.Message == diagnostic.Message))
        {
            diagnostics.Add(diagnostic);
        }
    }

    private static string FormatSourceKeys(IEnumerable<SourceInfo> sources) =>
        string.Join(", ", sources.Select(source => $"\"{RedactLabel(source.Key)}\"").Order(StringComparer.Ordinal));

    private static string FormatSourceKeys(IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(key => $"\"{RedactLabel(key)}\"").Order(StringComparer.Ordinal));

    private static string FormatPatterns(IEnumerable<string> patterns) =>
        string.Join(", ", patterns.Select(pattern => $"\"{FormatPattern(pattern)}\"").Order(StringComparer.Ordinal));

    private static string FormatPattern(string value)
    {
        var normalized = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (normalized.Length > 0 &&
            normalized.Length <= 128 &&
            normalized.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '*') &&
            (!normalized.Contains('*') || normalized.EndsWith('*')))
        {
            return normalized;
        }

        return RedactLabel(normalized);
    }

    private static bool IsInsecureHttpSource(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
             string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
    }

    internal static string RedactLabel(string value)
    {
        var normalized = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (normalized.Length == 0)
        {
            return "source";
        }

        if (IsSafeLabel(normalized))
        {
            return normalized;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return $"source-{hash[..12]}";
    }

    private static bool IsSafeLabel(string value)
    {
        if (value.Length > 128 ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.Contains('?') ||
            value.Contains('#') ||
            value.Contains('@') ||
            Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return false;
        }

        var sensitiveRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetTempPath()
        };
        if (sensitiveRoots.Any(root => !string.IsNullOrWhiteSpace(root) &&
            value.Contains(root, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Environment.UserName) &&
            value.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
    }
}

internal sealed class EffectiveConfig
{
    private readonly PackageSourceMapping _mapping;

    private EffectiveConfig(
        PackageSourceMapping mapping,
        IReadOnlyList<SourceInfo> sources,
        IReadOnlyList<MappingPattern> mappings)
    {
        _mapping = mapping;
        Sources = sources;
        Mappings = mappings;
        ActiveSources = sources.Where(source => source.IsEnabled).OrderBy(source => source.Key, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<SourceInfo> Sources { get; }
    public IReadOnlyList<SourceInfo> ActiveSources { get; }
    public IReadOnlyList<MappingPattern> Mappings { get; }
    public bool MappingEnabled => _mapping.IsEnabled;

    public static EffectiveConfig Load(string restoreContext, string repositoryRoot, string? configPath)
    {
        try
        {
            var configPaths = configPath is null
                ? DiscoverHierarchyConfigPaths(restoreContext)
                : [GetExplicitConfigPath(configPath)];

            foreach (var path in configPaths)
            {
                ValidateXmlFile(path, InputLimits.MaxConfigBytes);
            }

            using var settingsLoadingContext = new SettingsLoadingContext();
            var settings = Settings.LoadImmutableSettingsGivenConfigPaths(configPaths.ToList(), settingsLoadingContext);

            var packageSourceItems = settings.GetSection("packageSources")?.Items.OfType<SourceItem>().ToArray() ?? [];
            var loadedSources = new PackageSourceProvider(settings).LoadPackageSources().ToArray();
            if (loadedSources.Length > 256)
            {
                throw new AnalysisException("the effective NuGet configuration contains too many package sources.");
            }

            var sources = loadedSources
                .Select(source =>
                {
                    var sourceItem = packageSourceItems.FirstOrDefault(item =>
                        string.Equals(item.Key, source.Name, StringComparison.OrdinalIgnoreCase));
                    var configOrigin = sourceItem?.ConfigPath ?? string.Empty;
                    var provenance = Provenance.Classify(configOrigin, repositoryRoot);
                    return new SourceInfo(
                        source.Name,
                        source.Source,
                        source.IsEnabled,
                        provenance.Label,
                        provenance.IsRepositoryControlled);
                })
                .OrderBy(source => source.Key, StringComparer.Ordinal)
                .ToArray();

            var mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
            var mappingItems = new PackageSourceMappingProvider(settings).GetPackageSourceMappingItems();
            if (mappingItems.Count > 256)
            {
                throw new AnalysisException("the effective NuGet configuration contains too many source mappings.");
            }

            var mappings = mappingItems
                .SelectMany(item => item.Patterns.Select(pattern => new MappingPattern(item.Key, pattern.Pattern)))
                .ToArray();
            if (mappings.Length > 4096)
            {
                throw new AnalysisException("the effective NuGet configuration contains too many mapping patterns.");
            }

            foreach (var pattern in mappings)
            {
                PatternMatcher.Validate(pattern.Pattern);
            }

            return new(mapping, sources, mappings);
        }
        catch (AnalysisException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AnalysisException("NuGet configuration could not be read or is not valid.");
        }
    }

    public MappingSelection Select(string packageId)
    {
        try
        {
            if (!_mapping.IsEnabled)
            {
                return new(ActiveSources, []);
            }

            var configuredNames = _mapping.GetConfiguredPackageSources(packageId);
            var configuredSources = configuredNames
                .Select(name => Sources.FirstOrDefault(source =>
                    string.Equals(source.Key, name, StringComparison.OrdinalIgnoreCase)))
                .Where(source => source is not null)
                .Cast<SourceInfo>()
                .DistinctBy(source => source.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var matchingPatterns = Mappings
                .Where(mapping => PatternMatcher.Matches(mapping.Pattern, packageId))
                .Select(mapping => new MatchedPattern(
                    mapping,
                    Sources.FirstOrDefault(source =>
                        string.Equals(source.Key, mapping.SourceKey, StringComparison.OrdinalIgnoreCase)),
                    PatternMatcher.Specificity(mapping.Pattern)))
                .Where(item => item.Source is not null)
                .ToArray();

            var winners = matchingPatterns.Length == 0
                ? []
                : matchingPatterns
                    .Where(item => item.Score == matchingPatterns.Max(candidate => candidate.Score))
                    .Select(item => item.Source!)
                    .DistinctBy(source => source.Key, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            var winningPatterns = matchingPatterns.Length == 0
                ? []
                : matchingPatterns
                    .Where(item => item.Score == matchingPatterns.Max(candidate => candidate.Score))
                    .Select(item => item.Mapping.Pattern)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();

            var configuredSet = configuredSources.Select(source => source.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var winnerSet = winners.Select(source => source.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!configuredSet.SetEquals(winnerSet))
            {
                throw new AnalysisException("NuGet Package Source Mapping returned an eligibility set that could not be reconciled with its documented specificity rules.");
            }

            return new(winners.Where(source => source.IsEnabled).OrderBy(source => source.Key, StringComparer.Ordinal).ToArray(), winningPatterns);
        }
        catch (AnalysisException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AnalysisException("Package Source Mapping could not be evaluated safely.");
        }
    }

    private static void ValidateXmlFile(string path, int maxBytes)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > maxBytes)
        {
            throw new AnalysisException("a NuGet configuration file exceeds the supported input size.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxBytes
        };
        using var reader = XmlReader.Create(path, settings);
        var depth = 0;
        var elements = 0;
        while (reader.Read())
        {
            depth = Math.Max(depth, reader.Depth);
            if (depth > InputLimits.MaxXmlDepth)
            {
                throw new AnalysisException("a NuGet configuration file exceeds the supported XML depth.");
            }

            if (reader.NodeType == XmlNodeType.Element && ++elements > InputLimits.MaxXmlElements)
            {
                throw new AnalysisException("a NuGet configuration file contains too many XML elements.");
            }
        }
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            throw new AnalysisException("a supplied configuration path is invalid.");
        }
    }

    private static void EnsureFile(string path, string kind)
    {
        if (!File.Exists(path))
        {
            throw new AnalysisException($"the supplied {kind} could not be read.");
        }
    }

    private static string GetExplicitConfigPath(string configPath)
    {
        var fullConfigPath = SafeFullPath(configPath);
        EnsureFile(fullConfigPath, "NuGet configuration");
        return fullConfigPath;
    }

    private static string[] DiscoverHierarchyConfigPaths(string restoreContext)
    {
        var paths = new List<string>();
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var current = SafeFullPath(restoreContext);
        while (!string.IsNullOrWhiteSpace(current))
        {
            var configPath = Settings.OrderedSettingsFileNames
                .Select(fileName => Path.Combine(current, fileName))
                .FirstOrDefault(File.Exists);
            if (configPath is not null)
            {
                paths.Add(configPath);
            }

            var parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            current = parent.FullName;
        }

        var userSettingsDirectory = GetUserSettingsDirectory();
        if (userSettingsDirectory is not null)
        {
            AddIfFileExists(paths, Path.Combine(userSettingsDirectory, Settings.DefaultSettingsFileName));
            var additionalDirectory = Path.Combine(userSettingsDirectory, "config");
            if (Directory.Exists(additionalDirectory))
            {
                foreach (var pattern in Settings.SupportedMachineWideConfigExtension)
                {
                    paths.AddRange(Directory.EnumerateFiles(additionalDirectory, pattern, SearchOption.TopDirectoryOnly)
                        .Where(path => !comparer.Equals(Path.GetFileName(path), Settings.DefaultSettingsFileName))
                        .Order(comparer));
                }
            }
        }

        var machineConfigDirectory = GetMachineConfigDirectory();
        if (machineConfigDirectory is not null && Directory.Exists(machineConfigDirectory))
        {
            foreach (var pattern in Settings.SupportedMachineWideConfigExtension)
            {
                paths.AddRange(Directory.EnumerateFiles(machineConfigDirectory, pattern, SearchOption.TopDirectoryOnly)
                    .Order(comparer));
            }
        }

        return paths.Distinct(comparer).ToArray();
    }

    private static void AddIfFileExists(List<string> paths, string path)
    {
        if (File.Exists(path))
        {
            paths.Add(path);
        }
    }

    private static string? GetUserSettingsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }

            return string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, "NuGet");
        }

        var home = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".nuget", "NuGet");
    }

    private static string? GetMachineConfigDirectory()
    {
        string? root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetEnvironmentVariable("PROGRAMFILES(X86)");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Environment.GetEnvironmentVariable("PROGRAMFILES");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = "/Library/Application Support";
        }
        else
        {
            root = Environment.GetEnvironmentVariable("NUGET_COMMON_APPLICATION_DATA");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = "/etc/opt";
            }
        }

        return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, "NuGet", "Config");
    }
}

internal static class Provenance
{
    public static (string Label, bool IsRepositoryControlled) Classify(string configPath, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return ("unknown inherited configuration", false);
        }

        var fullConfigPath = ResolvePhysicalPath(configPath);
        if (IsWithinDirectory(fullConfigPath, ResolvePhysicalPath(repositoryRoot)))
        {
            return ("repository-controlled configuration", true);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData) && IsWithinDirectory(fullConfigPath, ResolvePhysicalPath(appData)))
        {
            return ("user-inherited configuration", false);
        }

        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonData) && IsWithinDirectory(fullConfigPath, ResolvePhysicalPath(commonData)))
        {
            return ("machine-inherited configuration", false);
        }

        return ("external inherited configuration", false);
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             !string.Equals(relative, "..", StringComparison.Ordinal) &&
             !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
             !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string ResolvePhysicalPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            var fileTarget = file.ResolveLinkTarget(returnFinalTarget: true);
            if (fileTarget is not null)
            {
                return Path.GetFullPath(fileTarget.FullName);
            }

            var parent = file.Directory;
            if (parent is null)
            {
                return fullPath;
            }

            var parentTarget = parent.ResolveLinkTarget(returnFinalTarget: true);
            return parentTarget is null
                ? fullPath
                : Path.Combine(Path.GetFullPath(parentTarget.FullName), file.Name);
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }
}

internal static class PatternMatcher
{
    public static bool Matches(string pattern, string value)
    {
        Validate(pattern);
        if (pattern == "*")
        {
            return true;
        }

        if (pattern.EndsWith('*'))
        {
            return value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
    }

    public static int Specificity(string pattern)
    {
        Validate(pattern);
        return pattern == "*" ? 0 : pattern.EndsWith('*') ? pattern.Length - 1 : 1_000_000;
    }

    public static void Validate(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Count(character => character == '*') > 1 ||
            (pattern.Contains('*') && !pattern.EndsWith('*')))
        {
            throw new AnalysisException("a package mapping or policy pattern is unsupported or invalid.");
        }
    }
}

internal static class TargetResolver
{
    private static readonly Regex SolutionProjectLine = new(
        "^Project\\(.*\\)\\s*=\\s*\"[^\"]+\",\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> SupportedProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj",
        ".fsproj",
        ".vbproj"
    };

    public static ProjectTarget Resolve(string? suppliedPath)
    {
        var input = suppliedPath is null ? Directory.GetCurrentDirectory() : suppliedPath;
        string fullInput;
        try
        {
            fullInput = Path.GetFullPath(input);
        }
        catch
        {
            throw new InvocationException("the supplied path is invalid.");
        }

        if (File.Exists(fullInput))
        {
            var extension = Path.GetExtension(fullInput);
            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveSolution(fullInput);
            }

            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return new(FindRepositoryRoot(Path.GetDirectoryName(fullInput)!), [fullInput], Path.GetDirectoryName(fullInput)!);
            }

            if (SupportedProjectExtensions.Contains(extension))
            {
                return new(FindRepositoryRoot(Path.GetDirectoryName(fullInput)!), [fullInput], Path.GetDirectoryName(fullInput)!);
            }

            throw new InvocationException("the supplied path must identify a solution or project.");
        }

        if (!Directory.Exists(fullInput))
        {
            throw new InvocationException("the supplied solution or project could not be found.");
        }

        var solutions = Directory.EnumerateFiles(fullInput, "*.sln", SearchOption.TopDirectoryOnly).ToArray();
        if (solutions.Length == 1)
        {
            return ResolveSolution(solutions[0]);
        }

        var projects = SupportedProjectExtensions
            .SelectMany(extension => Directory.EnumerateFiles(fullInput, "*" + extension, SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .ToArray();
        return projects.Length switch
        {
            0 => throw new InvocationException("the current directory contains no solution or project."),
            1 => new(FindRepositoryRoot(fullInput), projects, Path.GetDirectoryName(projects[0])!),
            _ => throw new InvocationException("the current directory contains multiple projects; supply one solution or project path.")
        };
    }

    private static ProjectTarget ResolveSolution(string solutionPath)
    {
        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        var declaredProjects = File.ReadLines(solutionPath)
            .Select(line => SolutionProjectLine.Match(line))
            .Where(match => match.Success)
            .Select(match => Path.GetFullPath(Path.Combine(solutionDirectory, match.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(path => !string.IsNullOrEmpty(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (declaredProjects.Any(path => !SupportedProjectExtensions.Contains(Path.GetExtension(path))))
        {
            throw new InvocationException("the supplied solution declares an unsupported project member.");
        }

        if (declaredProjects.Any(path => !File.Exists(path)))
        {
            throw new InvocationException("the supplied solution declares a project member that could not be read.");
        }

        if (declaredProjects.Length == 0)
        {
            throw new InvocationException("the supplied solution contains no readable projects.");
        }

        return new(FindRepositoryRoot(solutionDirectory), declaredProjects, solutionDirectory);
    }

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var gitMarker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(start);
    }
}
