using KeelMatrix.Telemetry;

namespace KeelMatrix.FeedFence;

internal static class FeedFenceTelemetry
{
    public static void TrackActivation(AnalysisResult result) =>
        TrackActivation(
            result,
            static (toolName, toolType) => new Client(toolName, toolType).TrackActivation);

    internal static void TrackActivation(
        AnalysisResult result,
        Func<string, Type, Action> createActivationRequest)
    {
        ArgumentNullException.ThrowIfNull(createActivationRequest);

        if (!IsActivationEligible(result))
        {
            return;
        }

        createActivationRequest("feedfence", typeof(VersionInfo))();
    }

    internal static bool IsActivationEligible(AnalysisResult result) =>
        result.PackageCount > 0 && result.EffectiveSourcePolicyEvaluated;
}
