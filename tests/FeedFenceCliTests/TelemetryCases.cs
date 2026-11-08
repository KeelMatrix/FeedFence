namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{

    private static void RunTelemetryCases()
    {
        var telemetryResult = new AnalysisResult(
            0,
            6,
            3,
            true,
            2,
            true,
            [new SourceInfo("public", "file:///local", true, "repository-controlled configuration", true)],
            [new Diagnostic("FF005", DiagnosticSeverity.Warning, "synthetic warning")]);
        var telemetryActivationRequests = 0;
        string? telemetryToolName = null;
        Type? telemetryToolType = null;
        FeedFenceTelemetry.TrackActivation(
            telemetryResult,
            (toolName, toolType) =>
            {
                telemetryToolName = toolName;
                telemetryToolType = toolType;
                return () => telemetryActivationRequests++;
            });
        AssertEqual(1, telemetryActivationRequests, "eligible shared telemetry activation request");
        AssertEqual("feedfence", telemetryToolName, "shared telemetry tool name");
        AssertEqual(typeof(VersionInfo), telemetryToolType, "shared telemetry version type");
        var sharedTrackActivation = typeof(KeelMatrix.Telemetry.Client).GetMethod("TrackActivation", Type.EmptyTypes)
            ?? throw new InvalidOperationException("shared telemetry has no parameterless activation API");
        AssertEqual(0, sharedTrackActivation.GetParameters().Length, "shared telemetry activation payload parameter count");

        var noPackageResult = new AnalysisResult(0, 0, 1, false, 0, true, [], []);
        FeedFenceTelemetry.TrackActivation(
            noPackageResult,
            (_, _) => throw new InvalidOperationException("ineligible analysis requested telemetry"));
        AssertEqual(false, FeedFenceTelemetry.IsActivationEligible(noPackageResult), "no-package telemetry activation eligibility");

        var unevaluatedPolicyResult = new AnalysisResult(0, 6, 3, true, 2, false, [], []);
        FeedFenceTelemetry.TrackActivation(
            unevaluatedPolicyResult,
            (_, _) => throw new InvalidOperationException("ineligible analysis requested telemetry"));
        AssertEqual(false, FeedFenceTelemetry.IsActivationEligible(unevaluatedPolicyResult), "unevaluated source policy telemetry activation eligibility");

        var telemetryFailureEscaped = false;
        try
        {
            FeedFenceTelemetry.TrackActivation(
                telemetryResult,
                (_, _) => () => throw new InvalidOperationException("synthetic shared telemetry failure"));
        }
        catch (InvalidOperationException)
        {
            telemetryFailureEscaped = true;
        }
        AssertEqual(true, telemetryFailureEscaped, "FeedFence adapter delegates failure isolation to the shared client");
    }
}
