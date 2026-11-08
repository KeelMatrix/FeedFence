namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed partial class ProbeRunner
{
    private readonly List<string> _failures = [];
    private readonly List<string> _observations = [];
    private readonly bool _machineFixtureInUserLayer;
    private string _runRoot = string.Empty;
    private string _shippingTool = string.Empty;

    public ProbeRunner(bool machineFixtureInUserLayer)
    {
        _machineFixtureInUserLayer = OperatingSystem.IsMacOS() || machineFixtureInUserLayer;
    }

    public int Run()
    {
        var templateRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        if (!Directory.Exists(templateRoot))
        {
            throw new DirectoryNotFoundException($"Fixture corpus was not copied to {templateRoot}.");
        }

        var runRoot = Path.Combine(Path.GetTempPath(), "feedfence-phase0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        _runRoot = runRoot;
        _shippingTool = Path.Combine(FindRepositoryRoot(AppContext.BaseDirectory), "src", "KeelMatrix.FeedFence", "bin", "Release", "net8.0", "KeelMatrix.FeedFence.dll");
        if (!File.Exists(_shippingTool))
        {
            throw new FileNotFoundException("Shipping FeedFence CLI was not built beside the Phase 0 probe.", _shippingTool);
        }

        try
        {
            CopyDirectory(templateRoot, runRoot);
            ReplaceTokens(runRoot, "__RUN_ROOT__", runRoot);
            InstallScopeTemplates(runRoot);
            PrepareFeeds(runRoot);

            using var environment = new ProbeEnvironment(runRoot);

            Console.WriteLine("FeedFence Phase 0 feasibility probe");
            Console.WriteLine("NuGet.Configuration: 7.9.0");
            Console.WriteLine("Restore mode: synthetic local file feeds only; network-capable sources and credential-provider paths are absent.");
            Console.WriteLine(_machineFixtureInUserLayer
                ? "SDK fixture layer: additional user config; the injected machine API is checked separately without host machine writes."
                : "SDK fixture layer: redirected native machine config; injected machine API checked separately.");
            Console.WriteLine();

            RunInjectedMachineControl(runRoot);
            RunHierarchyCases(runRoot);
            RunClearCase(runRoot);
            RunNoMappingCase(runRoot);
            RunCasingCase(runRoot);
            RunDisabledExactCase(runRoot);
            RunExplicitConfigCase(runRoot);
            RunProvenanceCases(runRoot);
            RunParseSafetyCases(runRoot);

            AssertTemplateCorpusUnchanged(templateRoot);

            Console.WriteLine();
            Console.WriteLine("Evidence summary");
            foreach (var observation in _observations)
            {
                Console.WriteLine($"- {observation}");
            }

            if (_failures.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("STOP: fixture assertions failed");
                foreach (var failure in _failures)
                {
                    Console.WriteLine($"- {failure}");
                }

                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("PASS (go): official NuGet APIs reproduce effective source and mapping values, independent restore markers prove eligible source outcomes, and source-item ConfigPath distinguishes repository, user, machine, and external origins for FF005.");
            Console.WriteLine("Provenance scope: classify only package-source declarations returned by NuGet by their official ConfigPath; do not re-merge settings or reimplement pattern matching.");
            Console.WriteLine("Read-only note: NuGet 7.9.0 can materialize a missing user NuGet.Config while loading default settings; the probe contains that side effect only inside its disposable run directory.");
            return 0;
        }
        finally
        {
            TryDelete(runRoot);
        }
    }
}
