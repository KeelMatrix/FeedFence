namespace KeelMatrix.FeedFence.CliTests;

internal sealed record TestCase(string Root, string Project, string? Solution, string? Config);
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}
