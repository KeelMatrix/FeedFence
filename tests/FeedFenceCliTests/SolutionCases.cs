using System.Text.Json;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{
    private void RunSolutionIdentityCases()
    {
        var slnx = CreateSlnxCase();
        var slnxResult = Run("check", slnx.Solution!, "--config", slnx.Config!);
        AssertEqual(0, slnxResult.ExitCode, "slnx solution extension exit code");

        var caseOnly = CreateCaseOnlySolution();
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("Case-sensitive solution member fixture: deferred to non-Windows CI.");
        }
        else
        {
            var caseOnlyResult = Run("check", caseOnly.Solution!, "--config", caseOnly.Config!);
            AssertEqual(1, caseOnlyResult.ExitCode, "case-sensitive solution member exit code");
            AssertContains(caseOnlyResult.Output, "FF001");
        }
    }

    private void RunNestedInputCases()
    {
        var nestedProject = CreateNestedProjectConfigCase();
        var nestedProjectResult = Run("check", nestedProject.Project);
        AssertEqual(1, nestedProjectResult.ExitCode, "nested project configuration scope exit code");
        AssertContains(nestedProjectResult.Output, "FF001");
        using (var nestedProjectJson = JsonDocument.Parse(Run("check", nestedProject.Project, "--format", "json").StandardOutput))
        {
            AssertEqual(
                true,
                nestedProjectJson.RootElement.GetProperty("sources").EnumerateArray().All(source => source.GetProperty("repositoryControlled").GetBoolean()),
                "nested project configuration provenance");
        }

        var nestedSolution = CreateNestedSolutionConfigCase();
        var nestedSolutionResult = Run("check", nestedSolution.Solution!);
        AssertEqual(1, nestedSolutionResult.ExitCode, "nested solution configuration scope exit code");
        AssertContains(nestedSolutionResult.Output, "FF001");

        var worktreePolicy = CreateWorktreePolicyCase();
        var worktreePolicyResult = Run("check", worktreePolicy.Project);
        AssertEqual(1, worktreePolicyResult.ExitCode, ".git file repository policy exit code");
        AssertContains(worktreePolicyResult.Output, "FF007");
    }

    private void RunSolutionMemberCases()
    {
        var dottedSolutionFolder = CreateDottedSolutionFolderCase();
        var dottedSolutionFolderResult = Run("check", dottedSolutionFolder.Solution!);
        AssertEqual(0, dottedSolutionFolderResult.ExitCode, "dotted solution folder exit code");

        var unsupportedSolutionMember = CreateUnsupportedSolutionMemberCase();
        var unsupportedSolutionMemberResult = Run("check", unsupportedSolutionMember.Solution!);
        AssertEqual(2, unsupportedSolutionMemberResult.ExitCode, "unsupported real solution member exit code");
        AssertContains(unsupportedSolutionMemberResult.Output, "unsupported project member");

        var partialSolution = CreatePartialSolutionCase();
        var partialSolutionResult = Run("check", partialSolution.Solution!);
        AssertEqual(2, partialSolutionResult.ExitCode, "partial solution exit code");
        AssertContains(partialSolutionResult.Output, "declares a project member that could not be read");

        var mixedSolution = CreateMixedSolutionCase();
        var mixedSolutionResult = Run("check", mixedSolution.Solution!);
        AssertEqual(1, mixedSolutionResult.ExitCode, "mixed solution member exit code");
        AssertContains(mixedSolutionResult.Output, "FF002");
    }
}
