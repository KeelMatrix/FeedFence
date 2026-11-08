using System.Text;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{
    private void RunStrictPolicySchemaCases()
    {
        var fixture = CreateCase("strict-policy-schema", ["Company.Internal"],
            ["<packageSourceMapping><packageSource key=\"public\"><package pattern=\"Company.*\" /></packageSource></packageSourceMapping>"],
            ["public"]);
        var policies = new Dictionary<string, string>
        {
            ["unknown-root-member"] = "{\"version\":1,\"privatePackage\":[\"Company.*\"]}",
            ["duplicate-root-member"] = "{\"version\":1,\"privatePackages\":[],\"privatePackages\":[\"Company.*\"]}",
            ["unknown-source-member"] = "{\"version\":1,\"sourceTrust\":{\"public\":{\"trust\":\"public\",\"unexpected\":\"value\"}}}",
            ["duplicate-trust-selector"] = "{\"version\":1,\"sourceTrust\":{\"public\":{\"trust\":\"public\",\"trust\":\"private\"}}}",
            ["conflicting-trust-selectors"] = "{\"version\":1,\"sourceTrust\":{\"public\":{\"trust\":\"public\",\"label\":\"private\"}}}",
            ["unknown-rule-member"] = "{\"version\":1,\"privatePackages\":[{\"pattern\":\"Company.*\",\"unexpected\":true}]}",
            ["duplicate-rule-selector"] = "{\"version\":1,\"privatePackages\":[{\"pattern\":\"Company.*\",\"pattern\":\"Other.*\"}]}",
            ["unknown-allowed-source-member"] = "{\"version\":1,\"privatePackages\":[{\"pattern\":\"Company.*\",\"allowedSources\":[\"public\"],\"unexpected\":true}]}",
            ["duplicate-allowed-sources-member"] = "{\"version\":1,\"privatePackages\":[{\"pattern\":\"Company.*\",\"allowedSources\":[\"public\"],\"allowedSources\":[\"other\"]}]}",
            ["unknown-exception-member"] = "{\"version\":1,\"exceptions\":[{\"code\":\"FF007\",\"sourceKey\":\"public\",\"reason\":\"test\",\"unexpected\":true}]}",
            ["duplicate-exception-selector"] = "{\"version\":1,\"exceptions\":[{\"code\":\"FF007\",\"sourceKey\":\"public\",\"sourceKey\":\"other\",\"reason\":\"test\"}]}",
        };

        foreach (var policy in policies)
        {
            var path = Path.Combine(fixture.Root, policy.Key + ".json");
            File.WriteAllText(path, policy.Value, Encoding.UTF8);
            var result = Run("check", fixture.Project, "--config", fixture.Config!, "--policy", path);
            AssertEqual(2, result.ExitCode, $"strict policy schema {policy.Key} exit code");
            AssertEqual(string.Empty, result.StandardOutput, $"strict policy schema {policy.Key} stdout");
        }

        var valid = Path.Combine(fixture.Root, "valid-policy.json");
        File.WriteAllText(valid, "{\"version\":1,\"sourceTrust\":{\"public\":\"public\"},\"privatePackages\":[\"Company.*\"]}", Encoding.UTF8);
        AssertEqual(1, Run("check", fixture.Project, "--config", fixture.Config!, "--policy", valid).ExitCode, "valid policy control");
    }

    private void RunPrivatePolicyCases()
    {
        var protectedCase = CreateCase("protected", ["Company.Internal"], [], ["public"]);
        var policy = Path.Combine(protectedCase.Root, "feedfence.json");
        File.WriteAllText(policy, "{\"version\":1,\"sourceTrust\":{\"public\":\"public\"},\"privatePackages\":[\"Company.*\"]}", Encoding.UTF8);
        AssertProcess(1, "check", protectedCase.Project, "--config", protectedCase.Config!, "--policy", policy);
        var exceptionPolicy = Path.Combine(protectedCase.Root, "exception.json");
        File.WriteAllText(exceptionPolicy, "{\"version\":1,\"sourceTrust\":{\"public\":\"public\"},\"privatePackages\":[\"Company.*\"],\"exceptions\":[{\"code\":\"FF007\",\"packagePattern\":\"Company.Internal\",\"sourceKey\":\"public\",\"reason\":\"synthetic test exception\"}]}", Encoding.UTF8);
        AssertProcess(0, "check", protectedCase.Project, "--config", protectedCase.Config!, "--policy", exceptionPolicy);
    }

    private void RunPolicySelectorCases()
    {
        var wrongTypedSelector = CreateCase("wrong-typed-selector", ["Company.Internal"],
            ["<packageSourceMapping><packageSource key=\"public\"><package pattern=\"Company.*\" /></packageSource></packageSourceMapping>"],
            ["public"]);
        var wrongTypedPolicy = Path.Combine(wrongTypedSelector.Root, "wrong-typed.json");
        File.WriteAllText(
            wrongTypedPolicy,
            "{\"version\":1,\"sourceTrust\":{\"public\":\"public\",\"private\":\"private\"},\"privatePackages\":[\"Company.*\"],\"exceptions\":[{\"code\":\"FF007\",\"packagePattern\":\"Company.*\",\"sourceKey\":17,\"reason\":\"invalid selector\"}]}",
            Encoding.UTF8);
        var wrongTypedSelectorResult = Run("check", wrongTypedSelector.Project, "--config", wrongTypedSelector.Config!, "--policy", wrongTypedPolicy);
        AssertEqual(2, wrongTypedSelectorResult.ExitCode, "wrong-typed selector exit code");
        AssertContains(wrongTypedSelectorResult.Output, "sourceKey");

        var conflictingSelectorPolicy = Path.Combine(wrongTypedSelector.Root, "conflicting-selector.json");
        File.WriteAllText(
            conflictingSelectorPolicy,
            "{\"version\":1,\"exceptions\":[{\"code\":\"FF007\",\"packagePattern\":\"Company.*\",\"pattern\":\"Company.*\",\"reason\":\"conflicting selectors\"}]}",
            Encoding.UTF8);
        var conflictingSelectorResult = Run("check", wrongTypedSelector.Project, "--config", wrongTypedSelector.Config!, "--policy", conflictingSelectorPolicy);
        AssertEqual(2, conflictingSelectorResult.ExitCode, "conflicting selector exit code");
        AssertContains(conflictingSelectorResult.Output, "conflicting package pattern selectors");

        var conflictingRulePolicy = Path.Combine(wrongTypedSelector.Root, "conflicting-rule.json");
        File.WriteAllText(
            conflictingRulePolicy,
            "{\"version\":1,\"sourceTrust\":{\"public\":\"public\"},\"privatePackages\":[{\"pattern\":\"Company.*\",\"packagePattern\":\"Other.*\"}]}",
            Encoding.UTF8);
        var conflictingRuleResult = Run("check", wrongTypedSelector.Project, "--config", wrongTypedSelector.Config!, "--policy", conflictingRulePolicy);
        AssertEqual(2, conflictingRuleResult.ExitCode, "conflicting package rule selector exit code");
        AssertContains(conflictingRuleResult.Output, "conflicting package pattern selectors");

        var conflictingSourceTrustPolicy = Path.Combine(wrongTypedSelector.Root, "conflicting-source-trust.json");
        File.WriteAllText(
            conflictingSourceTrustPolicy,
            "{\"version\":1,\"sourceTrust\":{\"public\":\"public\"},\"sources\":{\"public\":\"private\"},\"privatePackages\":[\"Company.*\"]}",
            Encoding.UTF8);
        var conflictingSourceTrustResult = Run("check", wrongTypedSelector.Project, "--config", wrongTypedSelector.Config!, "--policy", conflictingSourceTrustPolicy);
        AssertEqual(2, conflictingSourceTrustResult.ExitCode, "conflicting source trust selector exit code");
        AssertContains(conflictingSourceTrustResult.Output, "conflicting source trust selectors");

        var partiallyExcepted = CreateCase("partially-excepted", ["Company.Internal"],
            ["<packageSourceMapping><packageSource key=\"public\"><package pattern=\"Company.*\" /></packageSource><packageSource key=\"other\"><package pattern=\"Company.*\" /></packageSource></packageSourceMapping>"],
            ["public", "other"]);
        var partiallyExceptedPolicy = Path.Combine(partiallyExcepted.Root, "partial-exception.json");
        File.WriteAllText(
            partiallyExceptedPolicy,
            "{\"version\":1,\"sourceTrust\":{\"public\":\"public\",\"other\":\"public\"},\"privatePackages\":[\"Company.*\"],\"exceptions\":[{\"code\":\"FF002\",\"packagePattern\":\"Company.*\",\"reason\":\"accepted ambiguity\"},{\"code\":\"FF007\",\"packagePattern\":\"Company.*\",\"sourceKey\":\"public\",\"reason\":\"approved source\"}]}",
            Encoding.UTF8);
        var partiallyExceptedResult = Run("check", partiallyExcepted.Project, "--config", partiallyExcepted.Config!, "--policy", partiallyExceptedPolicy);
        AssertEqual(1, partiallyExceptedResult.ExitCode, "partially excepted multi-source exit code");
        AssertContains(partiallyExceptedResult.Output, "\"other\"");
        AssertNotContains(partiallyExceptedResult.Output, "source keys \"public\", \"other\"");
    }
}
