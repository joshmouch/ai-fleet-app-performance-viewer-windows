using System.Diagnostics;

namespace PerformanceViewer.Web;

internal static class WindowsCheckoutCollector
{
    internal static async Task<string> Build(string checkout, string sourceRuntime, CancellationToken stop)
    {
        var project = Path.Combine(checkout, "PerformanceViewer.csproj");
        var output = Path.Combine(sourceRuntime, "Collector");
        Directory.CreateDirectory(output);
        var build = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = checkout, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "build", project, "--output", output, "--nologo", "--verbosity", "quiet",
            "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-target:Build", "-getProperty:TargetPath" })
            build.ArgumentList.Add(argument);
        using var compiler = Process.Start(build) ?? throw new IOException("The checkout collector build could not start.");
        var stdout = compiler.StandardOutput.ReadToEndAsync();
        var stderr = compiler.StandardError.ReadToEndAsync();
        try { await compiler.WaitForExitAsync(stop); }
        catch (OperationCanceledException)
        {
            if (!compiler.HasExited) compiler.Kill(entireProcessTree: true);
            await compiler.WaitForExitAsync();
            throw;
        }
        var lines = (await stdout).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var errors = await stderr;
        if (compiler.ExitCode != 0)
        {
            var diagnostics = lines.Concat(errors.Split('\n', StringSplitOptions.TrimEntries))
                .Where(line => line.Contains("error ", StringComparison.OrdinalIgnoreCase)).Distinct().Take(2).ToArray();
            throw new InvalidDataException(diagnostics.Length == 0
                ? $"The checkout collector build exited with code {compiler.ExitCode}."
                : $"The checkout collector build failed. {string.Join(" ", diagnostics)}");
        }
        var executable = lines.Where(path => path.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.ChangeExtension(path, ".exe")).FirstOrDefault(File.Exists);
        return executable ?? throw new FileNotFoundException("The checkout collector build did not produce its executable.", output);
    }
}
