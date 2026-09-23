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

        try
        {
            createActivationRequest("feedfence", typeof(VersionInfo))();
        }
        catch
        {
            // Telemetry is best effort and must never change analysis behavior.
        }
    }

    internal static bool IsActivationEligible(AnalysisResult result) =>
        result.PackageCount > 0 && result.EffectiveSourcePolicyEvaluated;
}
