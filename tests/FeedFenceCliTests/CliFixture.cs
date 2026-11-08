namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{
    private static readonly string[] CompanySecretDependencies = ["Company.Secret"];
    private static readonly string[] CompanySecretDependencyGroup = ["Company.Secret >= 1.0.0"];
    private static readonly string[] ReportFormats = ["text", "json", "sarif"];
    private static readonly string[] ValidIdentityPolicyPatterns = ["日本語*", "A.B-C_D", "aaa*", "A\u0301*"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "feedfence-cli-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _tool;

    public Fixture(string tool)
    {
        Directory.CreateDirectory(_root);
        _tool = tool;
        if (!File.Exists(_tool))
        {
            throw new InvalidOperationException("the shipping tool was not copied to the test output.");
        }
    }

    public void RunAll()
    {
        var specificity = RunHelpAndReports();
        RunTelemetryCases();
        RunMappingCases();
        RunSourceSecurityCases();
        RunPrivatePolicyCases();
        RunInputFailureCases(specificity);
        RunBasicGraphFailureCases();
        RunNestedInputCases();
        RunDependencyTargetCases();
        RunSolutionMemberCases();
        RunPolicySelectorCases();
        RunRestoreGraphCompletenessCases();
        RunStrictPolicySchemaCases();
        RunIdentityOutputCases();
        RunReportContractCases();
        RunSolutionIdentityCases();

    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
