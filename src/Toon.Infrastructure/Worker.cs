using System.Diagnostics;
using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed class Worker(Studio studio)
{
    public async Task Run(CancellationToken ct)
    {
        var config = studio.Config;
        using var workerLock = new FileStream(
            Path.Combine(config.Data, "worker.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        J.Write(
            Path.Combine(config.Data, "worker-info.json"),
            J.Node(
                new
                {
                    pid = Environment.ProcessId,
                    started_at = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O"),
                    data_root = config.Data,
                    import_roots = config.ImportRoots,
                    tts_character_limit = config.TtsLimit,
                    sfx_seconds_limit = config.SoundEffectLimit,
                    renderer_fingerprint = config.RendererFingerprint(),
                    worker_assembly_sha256 = J.FileHash(typeof(Worker).Assembly.Location),
                }
            )
        );
        var nextHeartbeat = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextHeartbeat)
            {
                studio.Store.Heartbeat();
                nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(config.HeartbeatSeconds);
            }

            var job = studio.Store.Claim();
            if (job == null)
            {
                await Task.Delay(500, ct);
                continue;
            }

            await Execute(job, ct);
        }
    }

    public async Task Execute(JsonNode job, CancellationToken stop)
    {
        var config = studio.Config;
        var id = job.Str("job_id");
        var directory = Path.Combine(config.Data, "jobs", id);
        Directory.CreateDirectory(directory);
        var timer = Stopwatch.StartNew();
        var stageTimes = new JsonObject();
        var currentStage = "preflight";
        var stageStart = 0.0;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var monitorStop = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            try
            {
                var next = DateTimeOffset.MinValue;
                while (!monitorStop.IsCancellationRequested)
                {
                    if (DateTimeOffset.UtcNow >= next)
                    {
                        studio.Store.Heartbeat();
                        studio.Store.ChangeJob(
                            id,
                            j =>
                            {
                                if (j.Str("state") is "running" or "cancelling")
                                {
                                    j["heartbeat"] = Store.Now;
                                    j["lease_until"] = DateTimeOffset
                                        .UtcNow.AddSeconds(config.LeaseSeconds)
                                        .ToString("O");
                                }
                            }
                        );
                        next = DateTimeOffset.UtcNow.AddSeconds(config.HeartbeatSeconds);
                    }

                    if (studio.Store.Job(id).Str("state") != "running")
                    {
                        cancelled.Cancel();
                    }
                    await Task.Delay(500, monitorStop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                cancelled.Cancel();
            }
        });
        try
        {
            if (job["job_kind"]?.ToString() == "sound_effect")
            {
                Stage("sound_effect");
                await studio.Sounds.Execute(job, cancelled.Token);
                return;
            }

            if (job["job_kind"]?.ToString() is "voice_clone" or "voice_preview")
            {
                Stage(job.Str("job_kind"));
                await studio.Voices.Execute(job, cancelled.Token);
                return;
            }

            if (job["job_kind"]?.ToString() == "library_prepare")
            {
                Stage("prepare_materials");
                await studio.Library.Prepare(job, cancelled.Token);

                return;
            }

            var ct = cancelled.Token;
            var snapshot = job["snapshot"]!;
            var p = snapshot["project"]!;
            J.Require(
                J.Hash(J.Canonical(snapshot)) == job.Str("snapshot_hash"),
                "VERIFY_FAILED",
                "Job snapshot changed"
            );
            studio.CheckDraft(p, snapshot["rigs"], snapshot["environments"]);
            if (snapshot["environments"] is JsonObject environments)
            {
                foreach (var environment in environments.Select(pair => pair.Value!))
                {
                    Environments.Validate(environment, config.Root);
                }
            }

            if (snapshot["rigs"] is JsonObject rigs)
            {
                foreach (var rig in rigs.Select(pair => pair.Value!))
                {
                    CharacterRigs.Validate(rig, config.Root);
                }
            }
            J.Require(
                config.RendererFingerprint() == snapshot.Str("renderer_fingerprint"),
                "ASSET_VERSION_MISMATCH",
                "Renderer changed since enqueue; create a new job"
            );
            J.Require(
                J.FileHash(Path.Combine(config.Root, "styles/warm_editorial_2d/manifest.json"))
                    == snapshot.Str("style_sha256"),
                "ASSET_VERSION_MISMATCH",
                "Style manifest changed since enqueue"
            );
            J.Write(Path.Combine(directory, "preflight-project.json"), p);
            var layoutProject = p.DeepClone();
            var modes = new JsonObject();
            foreach (var u in p.Items("scenes").SelectMany(s => s.Items("utterances")))
            {
                if (u["audio"]!.Str("mode") == "recording")
                {
                    var r = u["audio"]!["asset"]!;
                    var a = snapshot["assets"]![r.Str("asset_id") + "@" + r.Str("version")]!;
                    modes[u.Str("utterance_id")] = a["alignment"] == null ? "utterance" : "timed";
                }
            }

            layoutProject["_caption_mode_by_utterance"] = modes;
            J.Write(Path.Combine(directory, "preflight-project.json"), layoutProject);
            var layout = JsonNode.Parse(
                await Processes.Run(
                    config.Node,
                    [
                        Path.Combine(config.Root, "renderer/layout.mjs"),
                        Path.Combine(directory, "preflight-project.json"),
                    ],
                    config.Root,
                    ct
                )
            )!;
            if (layout["issues"]!.AsArray().FirstOrDefault() is JsonNode issue)
                throw new ToonException(issue.Str("code"), issue.Str("message"), issue.Str("pointer"));

            string Resolve(JsonNode r)
            {
                var a =
                    snapshot["assets"]![r.Str("asset_id") + "@" + r.Str("version")]
                    ?? throw new ToonException("ASSET_MISSING", "Asset absent from snapshot");
                var path = Path.Combine(config.Data, a.Str("relative_path"));
                J.Require(
                    File.Exists(path) && J.FileHash(path) == a.Str("sha256"),
                    "ASSET_MISSING",
                    "Snapshot asset missing or changed"
                );

                return path;
            }

            foreach (var a in snapshot["assets"]!.AsObject())
            {
                _ = Resolve(J.Node(new { asset_id = a.Value!.Str("asset_id"), version = a.Value!.Str("version") }));
            }
            var audio = new Dictionary<string, AudioInfo>();
            var media = new Media(config);
            Stage("speech");
            var newKeys = new HashSet<string>();
            var newCount = 0;
            foreach (
                var u in p.Items("scenes")
                    .SelectMany(s => s.Items("utterances"))
                    .Where(u => u["audio"]!.Str("mode") == "tts")
            )
            {
                var key = Speech.CacheKey(u.Str("text"), snapshot["voices"]![u.Str("utterance_id")]!);
                if (!studio.Speech.Cached(key) && newKeys.Add(key))
                {
                    newCount += J.Normalize(u.Str("text")).EnumerateRunes().Count();
                }
            }

            J.Require(
                newCount == 0 || job["request"]!.Bool("allow_new_tts"),
                "TTS_REQUIRED",
                "Speech cache changed; new TTS is disabled"
            );
            J.Require(
                newCount <= job["request"]!.Int("max_new_tts_characters"),
                "TTS_LIMIT_EXCEEDED",
                "Uncached speech exceeds authorized budget"
            );
            foreach (var u in p.Items("scenes").SelectMany(s => s.Items("utterances")))
            {
                ct.ThrowIfCancellationRequested();
                if (u["audio"]!.Str("mode") == "recording")
                {
                    var r = u["audio"]!["asset"]!;
                    var a = snapshot["assets"]![r.Str("asset_id") + "@" + r.Str("version")]!;
                    audio[u.Str("utterance_id")] = await media.Prepare(Resolve(r), a["alignment"]?.DeepClone(), ct);
                }
                else
                {
                    studio.Voices.CheckSnapshotVoice(snapshot["voices"]![u.Str("utterance_id")]!);
                    audio[u.Str("utterance_id")] = await studio.Speech.Get(
                        u,
                        snapshot["voices"]![u.Str("utterance_id")]!,
                        id,
                        ct
                    );
                }
            }

            J.Require(
                audio.Values.All(a => a.Envelope.Any(v => v > 0)),
                "VALIDATION_ERROR",
                "Mandatory speech recording is silent or below the speech threshold"
            );
            var report = await new Pipeline(config).Render(
                p,
                job.Str("project_id"),
                job.Int("revision"),
                audio,
                Resolve,
                directory,
                job.Str("quality"),
                Stage,
                ct,
                snapshot["rigs"],
                snapshot["environments"],
                job.Str("job_kind") == "studio_preview"
            );
            stageTimes[currentStage] = timer.Elapsed.TotalSeconds - stageStart;
            report["stage_seconds"] = stageTimes;
            report["memory_scope"] = "worker process peak only; excludes Chromium child processes";
            report["elapsed_seconds"] = timer.Elapsed.TotalSeconds;
            report["peak_working_set_bytes"] = Process.GetCurrentProcess().PeakWorkingSet64;
            report["platform"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            report["new_tts_characters_estimate"] = newCount;
            report["audio_cache_hits"] = audio.Count - newKeys.Count;
            J.Write(Path.Combine(directory, "validation-report.json"), report);
            if (job.Str("job_kind") == "studio_preview")
            {
                studio.PublishStudioPreview(job, directory, ct);
                return;
            }
            var names = new List<string>
            {
                "video.mp4",
                "cover.png",
                "project.json",
                "timeline.json",
                "captions.json",
                "captions.srt",
                "validation-report.json",
            };
            if (snapshot["character_review"] is JsonNode review)
            {
                J.Write(Path.Combine(directory, "character-review.json"), review);
                names.Add("character-review.json");
                names.AddRange(Directory.GetFiles(directory, "frame-*.png").Select(Path.GetFileName).OfType<string>());
            }

            if (snapshot["environment_review"] is JsonNode environmentReview)
            {
                J.Write(Path.Combine(directory, "environment-review.json"), environmentReview);
                names.Add("environment-review.json");
                names.AddRange(Directory.GetFiles(directory, "frame-*.png").Select(Path.GetFileName).OfType<string>());
            }

            if (job.Str("quality") == "final")
            {
                names.Add("project.zip");
            }

            if (job.Str("quality") == "final")
            {
                File.Copy(
                    Path.Combine(directory, "validation-report.json"),
                    Path.Combine(directory, "export/validation-report.json"),
                    true
                );
                File.Delete(Path.Combine(directory, "project.zip"));
                System.IO.Compression.ZipFile.CreateFromDirectory(
                    Path.Combine(directory, "export"),
                    Path.Combine(directory, "project.zip")
                );
            }

            var target = Path.Combine(config.Data, "artifacts", id);
            var publishing = Path.Combine(directory, "publish");
            Directory.CreateDirectory(publishing);
            foreach (var name in names)
            {
                File.Copy(Path.Combine(directory, name), Path.Combine(publishing, name), true);
            }
            var artifacts = names
                .Select(name =>
                {
                    var path = Path.Combine(target, name);
                    var staged = Path.Combine(publishing, name);

                    return J.Node(
                        new
                        {
                            artifact_id = "artifact_" + Guid.NewGuid().ToString("N"),
                            job_id = id,
                            project_id = job.Str("project_id"),
                            revision = job.Int("revision"),
                            name,
                            mime = name.EndsWith(".mp4") ? "video/mp4"
                            : name.EndsWith(".png") ? "image/png"
                            : name.EndsWith(".zip") ? "application/zip"
                            : name.EndsWith(".srt") ? "application/x-subrip"
                            : "application/json",
                            bytes = new FileInfo(staged).Length,
                            sha256 = J.FileHash(staged),
                            duration_seconds = name.EndsWith(".mp4") ? report.Num("duration_seconds") : (double?)null,
                            resource_uri = new Uri(path).AbsoluteUri,
                            local_path = path,
                        }
                    );
                })
                .ToArray();
            studio.Store.ChangeJob(
                id,
                j =>
                {
                    J.Require(
                        j.Str("state") == "running" && !ct.IsCancellationRequested,
                        "CANCELLED",
                        "Cancellation won publication race"
                    );
                    Directory.Move(publishing, target);
                    j["state"] = "succeeded";
                    j["stage"] = "verify";
                    j["progress"] = 1;
                    j["ended_at"] = Store.Now;
                    j["artifacts"] = new JsonArray(artifacts);
                }
            );
        }
        catch (Exception ex)
        {
            var error = ex as ToonException;
            var code =
                ex is OperationCanceledException || error?.Code == "CANCELLED"
                    ? "CANCELLED"
                    : error?.Code ?? "RENDER_FAILED";
            studio.Store.ChangeJob(
                id,
                j =>
                {
                    if (j.Str("state") == "succeeded")
                        return;

                    var wasCancelled = j.Str("state") == "cancelling" || code == "CANCELLED";
                    j["state"] = wasCancelled ? "cancelled" : "failed";
                    j["ended_at"] = Store.Now;
                    j["error"] = J.Node(
                        new
                        {
                            code = wasCancelled ? "CANCELLED" : code,
                            message = error?.Message
                                ?? (
                                    wasCancelled
                                        ? "Job was cancelled"
                                        : "Worker operation failed; see local diagnostics"
                                ),
                            retryable = error?.Retryable ?? false,
                            details = new { pointer = error?.Pointer ?? "" },
                        }
                    );
                }
            );
            Console.Error.WriteLine($"{id} {code}");
        }
        finally
        {
            monitorStop.Cancel();
            await monitor;
        }

        void Stage(string stage)
        {
            cancelled.Token.ThrowIfCancellationRequested();
            stageTimes[currentStage] = timer.Elapsed.TotalSeconds - stageStart;
            currentStage = stage;
            stageStart = timer.Elapsed.TotalSeconds;
            studio.Store.ChangeJob(
                id,
                j =>
                {
                    J.Require(j.Str("state") == "running", "CANCELLED", "Job is cancelling");
                    j["stage"] = stage;
                    j["progress"] = null;
                }
            );
            Console.Error.WriteLine($"{id} {stage}");
        }
    }
}
