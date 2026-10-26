using System.Text.Json.Nodes;
using Toon.Core;
using Toon.Infrastructure;

namespace Toon.Acceptance;

internal static class StudioAcceptance
{
    public static async Task Run(Config config)
    {
        await using var session = new WorkflowSession(config);
        var template = await session.Call("get_episode_template", new { });
        session.Check(
            template["schema"] != null && template["characters"]!.AsArray().Count >= 2,
            "episode schema and catalogs"
        );
        var brief = J.Read(Path.Combine(config.Root, "templates/episode.json"));
        var lines = brief["scenes"]![0]!["lines"]!.AsArray();
        foreach (var (index, name) in new[] { (0, "problem"), (1, "idea") })
        {
            var asset = await session.Call(
                "register_asset",
                new
                {
                    source_path = Path.Combine(config.Root, "examples/offline-dialogue", name + ".wav"),
                    kind = "audio",
                }
            );
            lines[index]!["audio"] = J.Node(
                new
                {
                    mode = "recording",
                    asset = new { asset_id = asset.Str("asset_id"), version = asset.Str("version") },
                }
            );
        }
        lines[0]!["text"] = "Опять весь вечер проверял сайты.";
        lines[1]!["text"] = "Давай оставим время для жизни.";
        brief["title"] = "Studio acceptance — recorded SAPI fixture, no paid requests";
        var created = await session.Call("create_episode", new { brief, idempotency_key = "studio-episode" });
        var id = created.Str("project_id");
        session.Check(
            created["readiness"]!.Int("estimated_new_tts_characters") == 0,
            "creation does not require new TTS"
        );
        var repeated = await session.Call("create_episode", new { brief, idempotency_key = "studio-episode" });
        session.Check(id == repeated.Str("project_id"), "episode creation is idempotent");
        var modified = brief.DeepClone();
        modified["title"] = "Different request";
        await session.Call(
            "create_episode",
            new { brief = modified, idempotency_key = "studio-episode" },
            "IDEMPOTENCY_CONFLICT"
        );
        var invalid = brief.DeepClone();
        invalid["scenes"]![0]!["lines"]![0]!["speaker"] = "missing";
        await session.Call(
            "create_episode",
            new { brief = invalid, idempotency_key = "bad-speaker" },
            "VALIDATION_ERROR"
        );
        invalid = brief.DeepClone();
        invalid["extra"] = true;
        await session.Call("create_episode", new { brief = invalid, idempotency_key = "extra" }, "VALIDATION_ERROR");
        var saved = await session.Call("get_project", new { project_id = id, revision = 1 });
        var original = J.Canonical(saved["project"]!);
        modified = brief.DeepClone();
        modified["scenes"]![0]!["camera"] = J.Node(new { shot = "two_shot" });
        var updated = await session.Call(
            "update_episode",
            new
            {
                project_id = id,
                expected_revision = 1,
                brief = modified,
            }
        );
        session.Check(updated.Int("revision") == 2, "editing produces revision 2");
        await session.Call(
            "update_episode",
            new
            {
                project_id = id,
                expected_revision = 1,
                brief,
            },
            "REVISION_CONFLICT"
        );
        var old = await session.Call("get_project", new { project_id = id, revision = 1 });
        session.Check(J.Canonical(old["project"]!) == original, "old revision preserved");
        var list = await session.Call("list_projects", new { limit = 1 });
        session.Check(
            list["items"]![0]!.Str("project_id") == id && list["items"]![0]!.Int("revision") == 2,
            "list latest project revisions"
        );
        await session.Call("list_projects", new { limit = 101 }, "VALIDATION_ERROR");
        var cancellation = await session.Call(
            "start_studio_preview",
            new
            {
                project_id = id,
                revision = 1,
                idempotency_key = "cancel-studio",
            }
        );
        await session.Call("open_studio_preview", new { job_id = cancellation.Str("job_id") }, "PREVIEW_NOT_READY");
        await session.Call("cancel_job", new { job_id = cancellation.Str("job_id") });
        session.Check(
            (await session.Call("get_artifacts", new { job_id = cancellation.Str("job_id") })).AsArray().Count == 0,
            "cancelled preparation publishes nothing"
        );
        var request = new
        {
            project_id = id,
            revision = 2,
            idempotency_key = "studio-preview",
        };
        var job = await session.Call("start_studio_preview", request);
        var duplicate = await session.Call("start_studio_preview", request);
        session.Check(job.Str("job_id") == duplicate.Str("job_id"), "preview enqueue is idempotent");
        await session.Call(
            "start_render",
            new
            {
                project_id = id,
                revision = 2,
                quality = "preview",
                idempotency_key = "studio-preview",
            },
            "IDEMPOTENCY_CONFLICT"
        );
        await session.Disconnect();
        session.StartWorker();
        var ready = await session.Wait(job.Str("job_id"));
        session.Check(
            ready.Str("stage") == "studio_ready" && !ready["result"]!.Bool("video_exported"),
            "worker prepares after MCP disconnect without claiming video export"
        );
        var artifacts = await session.Call("get_artifacts", new { job_id = job.Str("job_id") });
        var folder = Path.GetDirectoryName(artifacts[0]!.Str("local_path"))!;
        session.Check(!File.Exists(Path.Combine(folder, "video.mp4")), "Studio preview has no fake MP4");
        var props = J.Read(Path.Combine(folder, "studio-props.json"));
        session.Check(
            props.Str("studio_audio") == "studio-mix.wav" && props["timeline"]!.Int("total_frames") > 30,
            "audio-backed props and measured timeline"
        );
        var opened = await session.Call("open_studio_preview", new { job_id = job.Str("job_id") });
        var reopened = await session.Call("open_studio_preview", new { job_id = job.Str("job_id") });
        session.Check(
            reopened.Bool("reused") && opened.Str("url") == reopened.Str("url"),
            "Studio reopens the same episode server"
        );
        using var http = new HttpClient();
        var response = await http.GetAsync(opened.Str("url"));
        session.Check(response.IsSuccessStatusCode, "Studio HTTP page is available");
        var mix = await http.GetByteArrayAsync(
            new Uri(new Uri(opened.Str("url")), opened.Str("static_base") + "/studio-mix.wav")
        );
        session.Check(
            mix.Length > 48000 && System.Text.Encoding.ASCII.GetString(mix, 0, 4) == "RIFF",
            "Studio serves the actual speech mix"
        );
        var path = Path.Combine(folder, "studio-props.json");
        var bytes = File.ReadAllBytes(path);
        File.AppendAllText(path, " ");
        await session.Call("open_studio_preview", new { job_id = job.Str("job_id") }, "VERIFY_FAILED");
        File.WriteAllBytes(path, bytes);
        var final = await session.Call(
            "start_render",
            new
            {
                project_id = id,
                revision = 2,
                quality = "preview",
                idempotency_key = "after-studio",
            }
        );
        await session.Wait(final.Str("job_id"));
        var video = (await session.Call("get_artifacts", new { job_id = final.Str("job_id") }))
            .AsArray()
            .Single(a => a!.Str("name") == "video.mp4")!;
        session.Check(
            new FileInfo(video.Str("local_path")).Length > 10000,
            "same revision exports a verified MP4 through worker"
        );
        session.Check(
            (await session.Call("close_studio_preview", new { job_id = job.Str("job_id") })).Bool("stopped"),
            "close only the managed Studio process"
        );
        session.Check(
            (await session.Call("studio_doctor", new { })).Bool("worker_alive"),
            "closing Studio leaves worker alive"
        );
        var restarted = await session.Call("open_studio_preview", new { job_id = job.Str("job_id") });
        session.Check(!restarted.Bool("reused"), "closed Studio starts again from preserved preview");
        await session.Call("close_studio_preview", new { job_id = job.Str("job_id") });
        J.Write(
            Path.Combine(config.Data, "studio-acceptance.json"),
            J.Node(
                new
                {
                    checks = session.Checks,
                    project_id = id,
                    preview_job_id = job.Str("job_id"),
                    studio = opened,
                    video,
                    paid_requests = 0,
                }
            )
        );
        Console.WriteLine("Studio acceptance: " + Path.Combine(config.Data, "studio-acceptance.json"));
    }
}
