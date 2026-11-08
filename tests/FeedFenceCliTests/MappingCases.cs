using System.Security;
using System.Text;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{

    private void RunMappingCases()
    {
        var equal = CreateCase("equal", ["Feed.Equal"],
            [
                "<packageSourceMapping><packageSource key=\"left\"><package pattern=\"Feed.Equal\" /></packageSource><packageSource key=\"right\"><package pattern=\"Feed.Equal\" /></packageSource></packageSourceMapping>",
            ],
            ["left", "right"]);
        AssertProcess(1, "check", equal.Project, "--config", equal.Config!);
        AssertContains(Run("check", equal.Project, "--config", equal.Config!).Output, "FF002");

        var noMapping = CreateCase("nomapping", ["Feed.One"], [], ["one", "two"]);
        AssertProcess(1, "check", noMapping.Project, "--config", noMapping.Config!);
        AssertContains(Run("check", noMapping.Project, "--config", noMapping.Config!).Output, "FF001");

        var unmapped = CreateCase("unmapped", ["Feed.Unmapped"],
            ["<packageSourceMapping><packageSource key=\"one\"><package pattern=\"Other.*\" /></packageSource></packageSourceMapping>"], ["one"]);
        AssertProcess(1, "check", unmapped.Project, "--config", unmapped.Config!);
        AssertContains(Run("check", unmapped.Project, "--config", unmapped.Config!).Output, "FF003");

        var casing = CreateCase("casing", ["Feed.Casing"],
            ["<packageSourceMapping><packageSource key=\"One\"><package pattern=\"Feed.Casing\" /></packageSource></packageSourceMapping>"], ["one"]);
        AssertProcess(0, "check", casing.Project, "--config", casing.Config!);
        AssertNotContains(Run("check", casing.Project, "--config", casing.Config!).Output, "FF004");

        var invalidMapping = CreateCase("invalid-mapping", ["Feed.Invalid"],
            ["<packageSourceMapping><packageSource key=\"missing\"><package pattern=\"Feed.Invalid\" /></packageSource></packageSourceMapping>"], ["one"]);
        var invalidMappingResult = Run("check", invalidMapping.Project, "--config", invalidMapping.Config!);
        AssertEqual(1, invalidMappingResult.ExitCode, "invalid mapping source identity exit code");
        AssertContains(invalidMappingResult.Output, "FF004");

        var wildcardAmbiguity = CreateCase("wildcard-ambiguity", ["Company.Internal"],
            ["<packageSourceMapping><packageSource key=\"left\"><package pattern=\"Company.*\" /></packageSource><packageSource key=\"right\"><package pattern=\"Company.*\" /></packageSource></packageSourceMapping>"], ["left", "right"]);
        var wildcardAmbiguityResult = Run("check", wildcardAmbiguity.Project, "--config", wildcardAmbiguity.Config!);
        AssertEqual(1, wildcardAmbiguityResult.ExitCode, "wildcard mapping ambiguity exit code");
        AssertContains(wildcardAmbiguityResult.Output, "Company.*");

        var disabledExact = CreateCase("disabled-exact", ["Feed.Disabled"], [], ["exact", "active"]);
        File.WriteAllText(
            disabledExact.Config!,
            "<configuration><packageSources><clear /><add key=\"exact\" value=\"" + SecurityElement.Escape(Path.Combine(disabledExact.Root, "feeds", "exact")) + "\" /><add key=\"active\" value=\"" + SecurityElement.Escape(Path.Combine(disabledExact.Root, "feeds", "active")) + "\" /></packageSources><disabledPackageSources><add key=\"exact\" value=\"true\" /></disabledPackageSources><packageSourceMapping><packageSource key=\"exact\"><package pattern=\"Feed.Disabled\" /></packageSource><packageSource key=\"active\"><package pattern=\"*\" /></packageSource></packageSourceMapping></configuration>",
            Encoding.UTF8);
        var disabledExactResult = Run("check", disabledExact.Project, "--config", disabledExact.Config!);
        AssertEqual(1, disabledExactResult.ExitCode, "disabled exact mapping exit code");
        AssertContains(disabledExactResult.Output, "FF003");
        AssertNotContains(disabledExactResult.Output, "could not be reconciled");
    }
}
