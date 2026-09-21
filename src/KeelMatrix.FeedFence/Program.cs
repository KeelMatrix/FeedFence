namespace KeelMatrix.FeedFence;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CliOptions.HelpText);
                return 0;
            }

            if (options.ShowVersion)
            {
                Console.WriteLine(VersionInfo.Current);
                return 0;
            }

            var result = FeedFenceAnalyzer.Analyze(options);
            Console.Write(TextReport.Render(result));
            return result.ExitCode;
        }
        catch (InvocationException exception)
        {
            Console.Error.WriteLine($"Invocation error: {exception.Message}");
            return 2;
        }
        catch (AnalysisException exception)
        {
            Console.Error.WriteLine($"Analysis error: {exception.Message}");
            return 2;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Analysis error: an unexpected failure prevented a trustworthy result.");
            return 2;
        }
    }
}

internal static class VersionInfo
{
    public const string Current = "0.1.0";
}

internal sealed class InvocationException : Exception
{
    public InvocationException(string message) : base(message) { }
}

internal sealed class AnalysisException : Exception
{
    public AnalysisException(string message) : base(message) { }
}

internal sealed record CliOptions(
    bool ShowHelp,
    bool ShowVersion,
    string? TargetPath,
    string? ConfigPath,
    string? PolicyPath,
    bool Strict)
{
    public static readonly string HelpText = string.Join(
        Environment.NewLine,
        [
            "feedfence check [path] [options]",
            "feedfence --help",
            "feedfence --version",
            "",
            "Checks already-restored package IDs against effective NuGet source policy.",
            "FeedFence never restores packages or contacts package feeds during analysis.",
            "",
            "Options:",
            "  --config <path>  Use this NuGet configuration instead of hierarchy discovery.",
            "  --policy <path>  Use this feedfence.json instead of the repository default.",
            "  --strict         Treat inherited active sources (FF005) as violations.",
            "  --format text    Select deterministic human-readable output (the v1 format).",
            "  -h, --help       Show this help.",
            "",
            "Exit codes:",
            "  0  Analysis completed and no policy violations were found.",
            "  1  One or more policy violations were found.",
            "  2  Invocation, configuration, restore-artifact, or analysis failure."
        ]) + Environment.NewLine;

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new InvocationException("the 'check' command is required; use --help for usage.");
        }

        if (args.Count == 1 && args[0] is "--help" or "-h")
        {
            return new(true, false, null, null, null, false);
        }

        if (args.Count == 1 && args[0] == "--version")
        {
            return new(false, true, null, null, null, false);
        }

        if (args[0] != "check")
        {
            throw new InvocationException("the command must be 'check'; use --help for usage.");
        }

        string? targetPath = null;
        string? configPath = null;
        string? policyPath = null;
        var strict = false;
        var format = "text";

        for (var index = 1; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h")
            {
                return new(true, false, null, null, null, false);
            }

            if (argument == "--strict")
            {
                strict = true;
                continue;
            }

            if (argument == "--version")
            {
                throw new InvocationException("--version cannot be combined with another command or option.");
            }

            if (argument.StartsWith("--format=", StringComparison.Ordinal))
            {
                format = argument[9..];
                continue;
            }

            if (argument is "--config" or "--policy" or "--format")
            {
                if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
                {
                    throw new InvocationException($"{argument} requires a value.");
                }

                if (argument == "--config")
                {
                    configPath = args[index];
                }
                else if (argument == "--policy")
                {
                    policyPath = args[index];
                }
                else
                {
                    format = args[index];
                }

                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvocationException($"unknown option '{argument}'.");
            }

            if (targetPath is not null)
            {
                throw new InvocationException("only one solution or project path may be supplied.");
            }

            targetPath = argument;
        }

        if (!string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvocationException("only --format text is available in this milestone.");
        }

        return new(false, false, targetPath, configPath, policyPath, strict);
    }
}

internal enum DiagnosticSeverity
{
    Violation,
    Warning,
    Information
}

internal sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? PackageId = null,
    IReadOnlyList<string>? SourceKeys = null);

internal sealed record AnalysisResult(
    int ExitCode,
    int PackageCount,
    int ActiveSourceCount,
    bool MappingEnabled,
    int DeterministicMappingCount,
    IReadOnlyList<SourceInfo> Sources,
    IReadOnlyList<Diagnostic> Diagnostics);

internal sealed record SourceInfo(
    string Key,
    string Value,
    bool IsEnabled,
    string Provenance,
    bool IsRepositoryControlled);

internal sealed record MappingPattern(string SourceKey, string Pattern);

internal sealed record MappingSelection(
    IReadOnlyList<SourceInfo> Sources,
    IReadOnlyList<string> WinningPatterns);

internal sealed record ProjectTarget(string RepositoryRoot, IReadOnlyList<string> ProjectPaths);

internal sealed record MatchedPattern(MappingPattern Mapping, SourceInfo? Source, int Score);

internal static class InputLimits
{
    public const int MaxConfigBytes = 2 * 1024 * 1024;
    public const int MaxXmlDepth = 64;
    public const int MaxXmlElements = 100_000;
}
