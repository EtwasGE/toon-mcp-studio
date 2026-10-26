using System.Diagnostics;
using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class Studio
{
    public void PublishStudioPreview(JsonNode job, string directory, CancellationToken ct)
    {
        var id = job.Str("job_id");
        var target = Path.Combine(Config.Data, "artifacts", id);
        var staging = Path.Combine(directory, "publish");
        Directory.CreateDirectory(staging);
        foreach (
            var name in new[]
            {
                "project.json",
                "timeline.json",
                "captions.json",
                "captions.srt",
                "studio-props.json",
                "validation-report.json",
            }
        )
        {
            File.Copy(Path.Combine(directory, name), Path.Combine(staging, name), true);
        }
        foreach (var source in Directory.GetFiles(Path.Combine(directory, "public"), "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(staging, Path.GetRelativePath(directory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(source, path, true);
        }
        J.Write(Path.Combine(staging, "public/toon-preview-id.json"), J.Node(new { job_id = id }));
        var files = Directory
            .GetFiles(staging, "*", SearchOption.AllDirectories)
            .Select(path =>
                J.Node(new { path = Path.GetRelativePath(staging, path).Replace('\\', '/'), sha256 = J.FileHash(path) })
            )
            .ToArray();
        var manifest = J.Node(
            new
            {
                kind = "studio_preview",
                project_id = job.Str("project_id"),
                revision = job.Int("revision"),
                job_id = id,
                video_exported = false,
                files,
            }
        );
        J.Write(Path.Combine(staging, "studio-preview.json"), manifest);
        var artifact = J.Node(
            new
            {
                artifact_id = "artifact_" + Guid.NewGuid().ToString("N"),
                job_id = id,
                project_id = job.Str("project_id"),
                revision = job.Int("revision"),
                name = "studio-preview.json",
                mime = "application/json",
                bytes = new FileInfo(Path.Combine(staging, "studio-preview.json")).Length,
                sha256 = J.FileHash(Path.Combine(staging, "studio-preview.json")),
                local_path = Path.Combine(target, "studio-preview.json"),
                resource_uri = new Uri(Path.Combine(target, "studio-preview.json")).AbsoluteUri,
            }
        );
        Store.ChangeJob(
            id,
            j =>
            {
                J.Require(
                    j.Str("state") == "running" && !ct.IsCancellationRequested,
                    "CANCELLED",
                    "Cancellation won preview publication race"
                );
                Directory.Move(staging, target);
                j["state"] = "succeeded";
                j["stage"] = "studio_ready";
                j["progress"] = 1;
                j["ended_at"] = Store.Now;
                j["artifacts"] = new JsonArray(artifact);
                j["result"] = manifest.DeepClone();
            }
        );
    }

    public async Task<JsonNode> OpenStudioPreview(string id, CancellationToken ct)
    {
        var job = Store.Job(id);
        J.Require(
            job.Str("state") == "succeeded" && job.Str("job_kind") == "studio_preview",
            "PREVIEW_NOT_READY",
            "Complete start_studio_preview before opening Studio"
        );
        J.Require(
            job["snapshot"]!.Str("renderer_fingerprint") == Config.RendererFingerprint(),
            "ASSET_VERSION_MISMATCH",
            "Renderer changed; prepare a new Studio preview"
        );
        var artifacts = Artifacts(id);
        var manifestPath = artifacts.AsArray().Single()!.Str("local_path");
        var directory = Path.GetDirectoryName(manifestPath)!;
        foreach (var file in J.Read(manifestPath).Items("files"))
        {
            var path = Path.GetFullPath(Path.Combine(directory, file.Str("path")));
            J.Require(
                path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(path)
                    && J.FileHash(path) == file.Str("sha256"),
                "VERIFY_FAILED",
                "Preview dependency is missing or changed"
            );
        }
        var session = Path.Combine(Config.Data, "studio", id);
        Directory.CreateDirectory(session);
        var psi = new ProcessStartInfo(Config.Node)
        {
            WorkingDirectory = Path.Combine(Config.Root, "renderer"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The detached Studio only needs local runtime paths, never provider credentials.
        var allowed = new[]
        {
            "PATH",
            "SystemRoot",
            "SystemDrive",
            "ProgramData",
            "ProgramFiles",
            "ProgramFiles(x86)",
            "CommonProgramFiles",
            "CommonProgramFiles(x86)",
            "PROCESSOR_ARCHITECTURE",
            "OS",
            "WINDIR",
            "COMSPEC",
            "PATHEXT",
            "TEMP",
            "TMP",
            "HOME",
            "USERPROFILE",
            "LOCALAPPDATA",
            "APPDATA",
            "LANG",
        };
        foreach (var name in psi.Environment.Keys.ToArray())
        {
            if (!allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                psi.Environment.Remove(name);
            }
        }
        foreach (var arg in new[] { Path.Combine(Config.Root, "renderer/studio.mjs"), directory, session, id })
        {
            psi.ArgumentList.Add(arg);
        }
        using var process =
            Process.Start(psi) ?? throw new ToonException("STUDIO_START_FAILED", "Could not start Studio launcher");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await error;
        J.Require(
            process.ExitCode == 0,
            "STUDIO_START_FAILED",
            "Studio did not start. See the local studio session log"
        );
        var result = JsonNode.Parse(await output)!;
        using (var server = Process.GetProcessById(result.Int("pid")))
        {
            J.Write(
                Path.Combine(session, "managed-process.json"),
                J.Node(new { pid = server.Id, start_ticks = server.StartTime.ToUniversalTime().Ticks })
            );
        }
        result["project_id"] = job.Str("project_id");
        result["revision"] = job.Int("revision");
        result["job_id"] = id;
        return result;
    }

    public JsonNode CloseStudioPreview(string id)
    {
        var job = Store.Job(id);
        J.Require(job.Str("job_kind") == "studio_preview", "VALIDATION_ERROR", "Expected a Studio preview job");
        var path = Path.Combine(Config.Data, "studio", id, "managed-process.json");
        if (!File.Exists(path))
            return J.Node(new { stopped = false });

        var identity = J.Read(path);
        try
        {
            using var process = Process.GetProcessById(identity.Int("pid"));
            J.Require(
                process.StartTime.ToUniversalTime().Ticks == identity["start_ticks"]!.GetValue<long>(),
                "STUDIO_PROCESS_CHANGED",
                "PID was reused; process was not stopped"
            );
            process.Kill(true);
            process.WaitForExit(5000);
            return J.Node(new { stopped = true });
        }
        catch (ArgumentException)
        {
            return J.Node(new { stopped = false });
        }
    }
}
