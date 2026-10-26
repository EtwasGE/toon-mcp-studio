using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Toon.Core;
using Toon.Infrastructure;

namespace Toon.Acceptance;

internal static class SoundAcceptance
{
    public static async Task Run(Config config)
    {
        Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", "fixture-only");
        var fixture = Path.Combine(config.Data, "effect.mp3");
        await Processes.Run(
            config.Ffmpeg,
            ["-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=1000:duration=0.5", fixture],
            config.Root,
            CancellationToken.None
        );
        var silentFixture = Path.Combine(config.Data, "silent.mp3");
        await Processes.Run(
            config.Ffmpeg,
            ["-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=mono", "-t", "1", silentFixture],
            config.Root,
            CancellationToken.None
        );
        var handler = new FakeSound(await File.ReadAllBytesAsync(fixture), await File.ReadAllBytesAsync(silentFixture));
        using var http = new HttpClient(handler);
        var studio = new Studio(config, http);
        void Check(bool ok, string name)
        {
            J.Require(ok, "TEST_FAILED", name);
            Console.WriteLine("PASS sounds " + name);
        }
        void Error(Action action, string code)
        {
            try
            {
                action();
            }
            catch (ToonException ex) when (ex.Code == code)
            {
                Check(true, code);
                return;
            }
            throw new Exception("Expected " + code);
        }
        Error(() => studio.Sounds.Start("tap", "tap", 1, false, 0, "denied"), "SFX_LIMIT_EXCEEDED");
        Error(() => studio.Sounds.Start("tap", "tap", 2, true, 1, "budget"), "SFX_LIMIT_EXCEEDED");
        var job = studio.Sounds.Start("tap", "tap", 1, true, 1, "one");
        Check(
            studio.Sounds.Start("tap", "tap", 1, true, 1, "one").Str("job_id") == job.Str("job_id"),
            "idempotent enqueue"
        );
        Error(() => studio.Sounds.Start("tap", "other", 1, true, 1, "one"), "IDEMPOTENCY_CONFLICT");
        await new Worker(studio).Execute(studio.Store.Claim()!, CancellationToken.None);
        var done = studio.Job(job.Str("job_id"));
        Check(done.Str("state") == "succeeded", "generated sound " + done["error"]);
        var asset = done["sound_asset"]!;
        Check(
            studio.Sounds.Catalog().AsArray().Count == 1 && studio.Artifacts(job.Str("job_id")).AsArray().Count == 1,
            "catalog and verified artifact"
        );
        Validation.Schema(done, Path.Combine(config.Root, "contracts/job.schema.json"));
        var reuse = studio.Sounds.Start("tap", "tap", 1, false, 0, "reuse");
        await new Worker(studio).Execute(studio.Store.Claim()!, CancellationToken.None);
        Check(
            studio.Job(reuse.Str("job_id")).Str("state") == "succeeded" && handler.Calls == 1,
            "reuse without paid call"
        );
        handler.Fail = true;
        var failed = studio.Sounds.Start("error", "error", 1, true, 1, "uncertain");
        await new Worker(studio).Execute(studio.Store.Claim()!, CancellationToken.None);
        Check(
            studio.Job(failed.Str("job_id"))["error"]!.Str("code") == "PROVIDER_OUTCOME_UNKNOWN",
            "uncertain outcome"
        );
        studio.Sounds.Start("error", "error", 1, true, 1, "retry");
        await new Worker(studio).Execute(studio.Store.Claim()!, CancellationToken.None);
        Check(handler.Calls == 2, "uncertain result never retried");

        handler.Fail = false;
        handler.Reject = true;
        var rejected = studio.Sounds.Start("restricted", "restricted", 1, true, 1, "restricted");
        await new Worker(studio).Execute(studio.Store.Claim()!, CancellationToken.None);
        var rejection = studio.Job(rejected.Str("job_id"));
        Check(
            rejection.Str("state") == "failed"
                && rejection["error"]!.Str("message").Contains("missing_permissions")
                && rejection["provider_usage_unknown"]!.AsArray().Count == 0,
            "401 diagnosed as rejected, not unknown billing"
        );
        Error(() => studio.Sounds.Start("bad", "bad", double.NaN, true, 1, "nan"), "VALIDATION_ERROR");
        Environment.SetEnvironmentVariable("TOON_MAX_SFX_SECONDS", "0");
        Error(() => studio.Sounds.Start("bad", "bad", 1, true, 1, "server-cap"), "SFX_LIMIT_EXCEEDED");
        Environment.SetEnvironmentVariable("TOON_MAX_SFX_SECONDS", null);

        handler.Reject = false;
        handler.Silent = true;
        var silentJob = studio.Sounds.Start("silent", "silent", 1, true, 1, "silent");
        await new Worker(studio).Execute(studio.Store.Claim()!, CancellationToken.None);
        Check(
            studio.Job(silentJob.Str("job_id"))["error"]!.Str("code") == "SFX_TOO_QUIET"
                && studio.Sounds.Catalog().AsArray().Count == 1,
            "silent generation excluded from ready library"
        );

        var reference = J.Node(new { asset_id = asset.Str("asset_id"), version = "1.0.0" });
        var brief = J.Read(Path.Combine(config.Root, "templates/episode.json"));
        brief["captions"] = J.Node(new { enabled = false });
        foreach (var line in brief.Items("scenes").SelectMany(s => s.Items("lines")))
        {
            line["audio"] = J.Node(new { mode = "recording", asset = reference.DeepClone() });
        }
        var first = brief["scenes"]![0]!;
        first["tail_ms"] = 2000;
        first["lines"]![0]!["gesture"] = "tap_phone";
        first["lines"]![0]!["sound"] = J.Node(new { asset = reference.DeepClone(), gain_db = -12 });
        var saved = studio.SaveEpisode(brief, "sound-episode");
        var project = studio.Store.Project(saved.Str("project_id"), 1)["project"]!;
        Check(!project["captions"]!.Bool("enabled"), "explicit captions disabled");
        Check(
            studio.Validate(saved.Str("project_id"), 1)["errors"]!.AsArray().Count == 0,
            "sound dependency validation"
        );
        var prepared = await new Media(config).Prepare(studio.Assets.Resolve(reference), null, CancellationToken.None);
        var audio = project
            .Items("scenes")
            .SelectMany(s => s.Items("utterances"))
            .ToDictionary(u => u.Str("utterance_id"), _ => prepared);
        var timeline = Compiler.Compile(project, saved.Str("project_id"), 1, audio);
        Check(timeline.Items("captions").Count() == 0, "no captions compiled");
        var gesture = timeline["scenes"]![0]!
            .Items("events")
            .First(e => e["action"]?["gesture_id"]?.ToString() == "tap_phone");
        Check(
            timeline["sounds"]![0]!["start_sample"]!.GetValue<long>() == (gesture.Int("frame") + 18) * 1600L,
            "contact frame sample sync"
        );
        Validation.Schema(timeline, Path.Combine(config.Root, "contracts/timeline.schema.json"));
        var interrupted = project.DeepClone();
        interrupted["scenes"]![0]!["events"]!
            .AsArray()
            .Add(
                J.Node(
                    new
                    {
                        event_id = "interrupt",
                        at = new { kind = "scene_start", offset_ms = 300 },
                        action = new
                        {
                            kind = "gesture",
                            character_id = "buyer",
                            gesture_id = "idle",
                        },
                    }
                )
            );
        Error(() => Compiler.Compile(interrupted, "test", 1, audio), "AMBIGUOUS_CUE");
        var directory = Path.Combine(config.Data, "sound-preview");
        await new Pipeline(config).Render(
            project,
            saved.Str("project_id"),
            1,
            audio,
            studio.Assets.Resolve,
            directory,
            "preview",
            _ => { },
            CancellationToken.None,
            prepareOnly: true
        );
        Check(File.Exists(Path.Combine(directory, "public", "studio-mix.wav")), "preview mixed audio");
        Check(
            J.Read(Path.Combine(directory, "timeline.json"))["sounds"]![0]!["audio_hash"]!.ToString() == prepared.Hash,
            "portable sound hash"
        );
        var isolated = timeline.DeepClone();
        isolated["utterances"] = new JsonArray();
        var isolatedWav = Path.Combine(config.Data, "isolated.wav");
        await new Media(config).Mix(
            isolated,
            audio,
            project,
            studio.Assets.Resolve,
            isolatedWav,
            CancellationToken.None
        );
        var pcm = Path.Combine(config.Data, "isolated.pcm");
        await Processes.Run(
            config.Ffmpeg,
            ["-v", "error", "-y", "-i", isolatedWav, "-f", "s16le", pcm],
            config.Root,
            CancellationToken.None
        );
        var bytes = await File.ReadAllBytesAsync(pcm);
        var start = (int)timeline["sounds"]![0]!["start_sample"]!.GetValue<long>();
        Check(bytes.Take(start * 2).All(b => b == 0), "PCM silent before contact");
        Check(bytes.Skip(start * 2).Take(4800).Any(b => b != 0), "PCM audible at contact");
        var finalDirectory = Path.Combine(config.Data, "sound-final");
        var report = await new Pipeline(config).Render(
            project,
            saved.Str("project_id"),
            1,
            audio,
            studio.Assets.Resolve,
            finalDirectory,
            "final",
            _ => { },
            CancellationToken.None
        );
        Check(report.Bool("verified"), "final MP4 verified");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(finalDirectory, "project.zip")))
        {
            Check(zip.GetEntry("media/" + prepared.Hash + ".wav") != null, "final ZIP includes sound");
        }
        Console.WriteLine("SFX final: " + Path.Combine(finalDirectory, "video.mp4"));
        project["scenes"]![0]!["events"]![0]!["sound"]!["offset_ms"] = 3000;
        Error(() => Compiler.Compile(project, "test", 1, audio), "VALIDATION_ERROR");
        Console.WriteLine("Sound acceptance completed; all HTTP was mocked.");
    }

    private sealed class FakeSound(byte[] audio, byte[] silence) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public bool Reject { get; set; }
        public bool Silent { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            J.Require(request.RequestUri!.AbsolutePath == "/v1/sound-generation", "TEST_FAILED", "SFX endpoint");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            J.Require(body.Str("model_id") == SoundEffects.Model, "TEST_FAILED", "SFX model");
            if (Reject)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = JsonContent.Create(new { detail = new { status = "missing_permissions" } }),
                };
            }
            return new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Silent ? silence : audio),
            };
        }
    }
}
