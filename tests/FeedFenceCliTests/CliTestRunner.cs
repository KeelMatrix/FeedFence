namespace KeelMatrix.FeedFence.CliTests;

internal static class CliTestRunner
{
    public static int Run(string[] args)
    {
        PolicyMetadataCases.Run();
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        Console.WriteLine($"CLI test process: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {architecture}");
        var expectedArchitecture = Environment.GetEnvironmentVariable("FEEDFENCE_TEST_ARCHITECTURE");
        if (expectedArchitecture is not null && !architecture.Equals(expectedArchitecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"expected native {expectedArchitecture}, actual {architecture}");
        if (args.Contains("--metadata-only", StringComparer.Ordinal)) return 0;
        var toolIndex = Array.IndexOf(args, "--tool");
        var tool = toolIndex < 0 ? Path.Combine(AppContext.BaseDirectory, "KeelMatrix.FeedFence.dll") : args[toolIndex + 1];
        if (args.Contains("--policy-runner-failure-control", StringComparer.Ordinal))
        {
            using var policyCases = new RestoreInputCases(tool);
            policyCases.PolicySpecialFiles(failureControl: true);
            return 0;
        }
        if (args.Contains("--policy-only", StringComparer.Ordinal))
        {
            using var policyCases = new RestoreInputCases(tool);
            policyCases.PolicyDiscovery();
            policyCases.PolicySpecialFiles();
            return 0;
        }
        if (args.Contains("--policy-objects-only", StringComparer.Ordinal))
        {
            using var policyCases = new RestoreInputCases(tool);
            policyCases.PolicySpecialFiles();
            return 0;
        }
        if (args.Contains("--private-flow-only", StringComparer.Ordinal))
        {
            using var privateCases = new RestoreInputCases(tool);
            privateCases.PrivateDependencyFlow();
            return 0;
        }
        if (!args.Contains("--families-only", StringComparer.Ordinal))
        {
            using var fixture = new Fixture(tool);
            fixture.RunAll();
        }
        if (!args.Contains("--legacy-only", StringComparer.Ordinal))
        {
            using var restoreCases = new RestoreInputCases(tool);
            restoreCases.RunAll();
        }
        Console.WriteLine("PASS: FeedFence CLI contract tests");
        return 0;
    }
}
