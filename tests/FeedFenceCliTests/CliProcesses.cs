using System.Diagnostics;
using System.Text;

namespace KeelMatrix.FeedFence.CliTests;

internal sealed partial class Fixture : IDisposable
{
    private ProcessResult Run(params string[] args) => Run(new Dictionary<string, string?>(), args);

    private ProcessResult Run(IReadOnlyDictionary<string, string?> environment, params string[] args)
    {
        var startInfo = new ProcessStartInfo(_tool.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : _tool)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["KEELMATRIX_NO_TELEMETRY"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        foreach (var entry in environment)
        {
            if (entry.Value is null)
            {
                startInfo.Environment.Remove(entry.Key);
            }
            else
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }
        if (_tool.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(_tool);
        }
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start the FeedFence process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new(process.ExitCode, output.Result, error.Result);
    }

    private ProcessResult RunExternal(string fileName, params string[] args)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["KEELMATRIX_NO_TELEMETRY"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["NUGET_PACKAGES"] = Path.Combine(_root, "restore-cross-check-packages");
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(_root, "restore-cross-check-home");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"could not start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new(process.ExitCode, output.Result, error.Result);
    }

    private void AssertProcess(int expectedExitCode, params string[] args)
    {
        var result = Run(args);
        if (result.ExitCode != expectedExitCode)
        {
            throw new InvalidOperationException($"{string.Join(' ', args)}: expected '{expectedExitCode}', actual '{result.ExitCode}'. stderr: {result.StandardError.Trim()} stdout: {result.StandardOutput.Trim()}");
        }
    }
}
