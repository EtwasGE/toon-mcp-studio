using System.Diagnostics;
using Toon.Core;

namespace Toon.Infrastructure;

public static class Processes
{
    public static async Task<string> Run(
        string executable,
        IEnumerable<string> args,
        string cwd,
        CancellationToken ct,
        string? logFile = null
    )
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-nostdin");
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        if (!OperatingSystem.IsWindows())
        {
            var originalArguments = psi.ArgumentList.ToArray();
            psi.FileName = Environment.GetEnvironmentVariable("TOON_NODE") ?? "node";
            psi.ArgumentList.Clear();
            psi.ArgumentList.Add(Path.Combine(cwd, "scripts/supervise.mjs"));
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(executable);
            foreach (var arg in originalArguments)
            {
                psi.ArgumentList.Add(arg);
            }
        }
        // Renderer and media tools do not need provider credentials.
        psi.Environment.Remove("ELEVENLABS_API_KEY");
        psi.Environment.Remove("TOON_VOICE_ALIASES");
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch
        {
            throw new ToonException("RENDER_FAILED", $"Cannot start {Path.GetFileName(executable)}");
        }

        process.StandardInput.Close();
        using var processTree = ProcessTree.Attach(process);
        if (logFile != null)
        {
            J.Write(
                logFile + ".process.json",
                J.Node(
                    new
                    {
                        pid = process.Id,
                        executable = Path.GetFileName(executable),
                        started_at = Store.Now,
                    }
                )
            );
        }
        using var cancel = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException) { }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var output = await stdout;
        var error = await stderr;
        if (logFile != null)
        {
            await File.WriteAllTextAsync(logFile, output + "\n" + error, CancellationToken.None);
        }

        if (process.ExitCode != 0)
            throw new ToonException(
                "RENDER_FAILED",
                $"{Path.GetFileName(executable)} exited with code {process.ExitCode}; see job diagnostics"
            );

        return output + "\n" + error;
    }
}
