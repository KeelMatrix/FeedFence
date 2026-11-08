using System.Diagnostics;

namespace KeelMatrix.FeedFence.Phase0Probe;

internal sealed partial class ProbeRunner
{
    private static RestoreResult RunRestore(string projectPath, string workingDirectory, string packageRoot, string? configPath, string runRoot)
    {
        var arguments = new List<string>
        {
            "restore",
            projectPath,
            "--packages",
            packageRoot,
            "--force",
            "--no-cache",
            "--disable-build-servers",
            "--disable-parallel",
            "--verbosity",
            "minimal"
        };
        if (configPath is not null)
        {
            arguments.Add("--configfile");
            arguments.Add(configPath);
        }

        var displayCommand = "dotnet " + string.Join(" ", arguments.Select(argument => argument.Replace(runRoot, "<fixture-root>", StringComparison.OrdinalIgnoreCase)));
        var start = Stopwatch.StartNew();
        var processStart = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            processStart.ArgumentList.Add(argument);
        }

        using var process = Process.Start(processStart) ?? throw new InvalidOperationException("Unable to start dotnet restore.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        start.Stop();

        var combined = (standardOutput + " " + standardError).Replace(runRoot, "<fixture-root>", StringComparison.OrdinalIgnoreCase).Replace("\r", " ").Replace("\n", " ").Trim();
        if (combined.Length > 500)
        {
            combined = combined[..500] + "...";
        }

        Console.WriteLine($"  command: {displayCommand}; exit={process.ExitCode}; duration={start.ElapsedMilliseconds} ms");
        return new RestoreResult(process.ExitCode, start.ElapsedMilliseconds, combined);
    }

    private ProcessResult RunShippingCheck(string projectPath, string? configPath)
    {
        var processStart = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processStart.Environment["KEELMATRIX_NO_TELEMETRY"] = "1";
        processStart.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        processStart.ArgumentList.Add(_shippingTool);
        processStart.ArgumentList.Add("check");
        processStart.ArgumentList.Add(projectPath);
        if (configPath is not null)
        {
            processStart.ArgumentList.Add("--config");
            processStart.ArgumentList.Add(configPath);
        }

        using var process = Process.Start(processStart) ?? throw new InvalidOperationException("Unable to start the shipping FeedFence CLI.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput + standardError);
    }
}
