namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed class ProbeEnvironment : IDisposable
{
    private readonly Dictionary<string, string?> _previous = [];

    public ProbeEnvironment(string runRoot)
    {
        var values = new Dictionary<string, string?>
        {
            ["APPDATA"] = Path.Combine(runRoot, "user-appdata"),
            ["USERPROFILE"] = Path.Combine(runRoot, "user-profile"),
            ["HOME"] = Path.Combine(runRoot, "user-profile"),
            ["NUGET_COMMON_APPLICATION_DATA"] = Path.Combine(runRoot, "machine-common"),
            ["PROGRAMFILES(X86)"] = Path.Combine(runRoot, "machine-common"),
            ["PROGRAMFILES"] = Path.Combine(runRoot, "machine-common"),
            ["NUGET_PACKAGES"] = Path.Combine(runRoot, "process-packages"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(runRoot, "http-cache"),
            ["DOTNET_CLI_HOME"] = Path.Combine(runRoot, "dotnet-home"),
            ["NUGET_PLUGIN_PATHS"] = string.Empty,
            ["NUGET_CREDENTIALPROVIDERS_PATH"] = string.Empty,
            ["NUGET_CERT_REVOCATION_MODE"] = "offline",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1"
        };

        foreach (var pair in values)
        {
            _previous[pair.Key] = Environment.GetEnvironmentVariable(pair.Key);
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    public void Dispose()
    {
        foreach (var pair in _previous)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
