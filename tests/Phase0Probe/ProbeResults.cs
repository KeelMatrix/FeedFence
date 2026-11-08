namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed record RestoreResult(int ExitCode, long DurationMs, string Output);

internal sealed record ControlledRestoreResult(bool Success, string? Marker);

internal sealed record ProcessResult(int ExitCode, string Output);
