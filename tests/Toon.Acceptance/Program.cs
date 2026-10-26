using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Toon.Acceptance;
using Toon.Core;
using Toon.Infrastructure;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
Environment.SetEnvironmentVariable("TOON_APP_ROOT", root);
var testRoot = Path.Combine(root, ".data", "acceptance-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
if (args.FirstOrDefault() == "--live-tts")
{
    testRoot = Environment.GetEnvironmentVariable("TOON_DATA_ROOT") ?? testRoot;
}
Environment.SetEnvironmentVariable("TOON_DATA_ROOT", testRoot);
Environment.SetEnvironmentVariable("TOON_IMPORT_ROOTS", Path.Combine(root, "examples"));
Environment.SetEnvironmentVariable("TOON_LEASE_SECONDS", "10");
if (args.FirstOrDefault() == "--live-tts")
{
    if (args.Length != 2 || !int.TryParse(args[1], out var liveLimit) || liveLimit is < 1 or > 1000)
        throw new ArgumentException(
            "Explicit paid mode: --live-tts MAX_CHARACTERS (1–1000), only after operator authorization"
        );

    await LiveSpeechAcceptance.Run(new Config(), liveLimit);
    return;
}
Environment.SetEnvironmentVariable("TOON_USE_USER_SECRETS", "0");

if (args.Contains("--secrets"))
{
    SecretsAcceptance.Run(testRoot);
    return;
}

// No paid HTTP client is called by this harness. Offline fixtures and injected transports only.
var config = new Config();
var studio = new Studio(config);
var results = new JsonArray();
var errors = 0;
if (args.Contains("--studio"))
{
    await StudioAcceptance.Run(config);
    return;
}
if (args.Contains("--workflow"))
{
    await WorkflowAcceptance.Run(config);
    return;
}

if (args.Contains("--voices"))
{
    await VoiceAcceptance.Run(config);
    return;
}

if (args.Contains("--environments"))
{
    await EnvironmentAcceptance.Run(config);
    return;
}

if (args.Contains("--characters"))
{
    await CharacterAcceptance.Run(config);
    return;
}

if (args.Contains("--library"))
{
    await LibraryAcceptance.Run(config);

    return;
}

if (args.FirstOrDefault() == "--verify-artifacts")
{
    var directory = Path.GetFullPath(args[1]);
    Validation.Schema(
        J.Read(Path.Combine(directory, "project.json")),
        Path.Combine(root, "contracts/project.schema.json")
    );
    Validation.Schema(
        J.Read(Path.Combine(directory, "timeline.json")),
        Path.Combine(root, "contracts/timeline.schema.json")
    );
    Console.WriteLine("PASS saved project and compiled timeline schemas");

    return;
}

if (args.Contains("--sync"))
{
    await SyncProbe.Run(config, Path.Combine(testRoot, "sync"));
    Console.WriteLine("PASS synchronization " + Path.Combine(testRoot, "sync/sync-report.json"));

    return;
}
void Pass(string id, string detail)
{
    Console.WriteLine("PASS " + id + " " + detail);
    results.Add(
        J.Node(
            new
            {
                id,
                status = "pass",
                detail,
            }
        )
    );
}
void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}
async Task Test(string id, Func<Task> work)
{
    try
    {
        await work();
        Pass(id, "Verified by acceptance harness");
    }
    catch (Exception ex)
    {
        errors++;
        Console.WriteLine("FAIL " + id + " " + ex.Message);
        results.Add(
            J.Node(
                new
                {
                    id,
                    status = "fail",
                    detail = ex.Message,
                }
            )
        );
    }
}
void Expect(string code, Action action)
{
    try
    {
        action();
        throw new Exception("Expected " + code);
    }
    catch (ToonException ex)
    {
        Assert(ex.Code == code, "Expected " + code + ", received " + ex.Code);
    }
}
async Task<McpClient> Connect() =>
    await McpClient.CreateAsync(
        new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "Toon acceptance",
                Command = "dotnet",
                Arguments = [Path.Combine(root, "src/Toon.Mcp/bin/Debug/net10.0/Toon.Mcp.dll")],
                WorkingDirectory = root,
            }
        ),
        new McpClientOptions
        {
            ProtocolVersion = "2026-07-28",
            DiscoverProbeTimeout = TimeSpan.FromSeconds(60),
            InitializationTimeout = TimeSpan.FromSeconds(90),
        }
    );
async Task<JsonNode> Call(McpClient client, string name, object args, bool failure = false)
{
    var dict = J.Node(args).AsObject().ToDictionary(x => x.Key, x => (object?)x.Value);
    var r = await client.CallToolAsync(name, dict);
    Assert(
        (r.IsError ?? false) == failure,
        name + ": " + string.Join("", r.Content.OfType<TextContentBlock>().Select(x => x.Text))
    );
    var data = JsonNode.Parse(r.StructuredContent!.Value.GetRawText())!;
    Assert(data.Bool("ok") == !failure, "Structured result differs from isError");

    return failure ? data["error"]! : data["data"]!;
}

var project = J.Read(Path.Combine(root, "examples/offline-dialogue/project.json"));
string projectId = "";
string previewJob = "";
string finalJob = "";
await using (var client = await Connect())
{
    await Test(
        "A17",
        async () =>
        {
            var tools = await client.ListToolsAsync();
            Assert(tools.Count >= 22, "Expected render and library SDK tools");
            foreach (var t in tools)
            {
                Assert(
                    t.ProtocolTool.OutputSchema != null
                        && t.ProtocolTool.OutputSchema.Value.TryGetProperty("oneOf", out _),
                    "Output success/error union absent"
                );
            }
            var doctor = await Call(client, "studio_doctor", new { });
            Assert(!doctor.Bool("worker_alive"), "Unexpected worker");
            await client.ReadResourceAsync("toon://styles/warm_editorial_2d/1.0.0/guide");
        }
    );
    foreach (var u in project.Items("scenes").SelectMany(s => s.Items("utterances")))
    {
        var file = u["audio"]!["asset"]!.Str("asset_id") + ".wav";
        var asset = await Call(
            client,
            "register_asset",
            new { source_path = Path.Combine(root, "examples/offline-dialogue", file), kind = "audio" }
        );
        u["audio"]!["asset"] = J.Node(new { asset_id = asset.Str("asset_id"), version = asset.Str("version") });
    }

    var screen = await Call(
        client,
        "register_asset",
        new { source_path = Path.Combine(root, "examples/offline-dialogue/screen.png"), kind = "image" }
    );
    project["scenes"]![1]!["overlays"]![0]!["content"]!["asset"] = J.Node(
        new { asset_id = screen.Str("asset_id"), version = screen.Str("version") }
    );
    var created = await Call(client, "create_project", new { project });
    projectId = created.Str("project_id");
    await Test(
        "A03",
        async () =>
        {
            var invalid = project.DeepClone();
            invalid["scenes"]![0]!["events"]![0]!["action"]!["gesture_id"] = "execute_code";
            var e = await Call(client, "create_project", new { project = invalid }, true);
            Assert(
                e.Str("code") == "UNSUPPORTED_ACTION" && e["details"]!.Str("pointer").StartsWith("/scenes/"),
                "Gesture should fail with pointer"
            );
            invalid = project.DeepClone();
            invalid["untrusted_script"] = "arbitrary";
            e = await Call(client, "create_project", new { project = invalid }, true);
            Assert(e.Str("code") == "VALIDATION_ERROR", "Unknown field accepted");
        }
    );
    await Test(
        "A03-missing",
        async () =>
        {
            var draft = project.DeepClone();
            draft["scenes"]![0]!["utterances"]![0]!["audio"]!["asset"]!["asset_id"] = "absent_recording";
            var p = await Call(client, "create_project", new { project = draft });
            var e = await Call(
                client,
                "start_render",
                new
                {
                    project_id = p.Str("project_id"),
                    revision = 1,
                    quality = "preview",
                    idempotency_key = "missing-dependency",
                },
                true
            );
            Assert(
                e.Str("code") == "ASSET_MISSING" && e["details"]!.Str("pointer").Contains("/audio/asset"),
                "Missing asset was not blocked with pointer"
            );
            var advertising = await Call(
                client,
                "create_project",
                new { project = J.Read(Path.Combine(root, "examples/novostroykin/project.json")) }
            );
            Assert(
                !advertising["readiness"]!.Bool("render_ready"),
                "Unconfigured real-interface example advertised ready"
            );
        }
    );
    await Test(
        "A05",
        async () =>
        {
            await Call(
                client,
                "update_project",
                new
                {
                    project_id = projectId,
                    expected_revision = 1,
                    project,
                }
            );
            var e = await Call(
                client,
                "update_project",
                new
                {
                    project_id = projectId,
                    expected_revision = 1,
                    project,
                },
                true
            );
            Assert(e.Str("code") == "REVISION_CONFLICT", "Stale update accepted");
        }
    );
    await Test(
        "A06",
        async () =>
        {
            var a = await Call(
                client,
                "start_render",
                new
                {
                    project_id = projectId,
                    revision = 2,
                    quality = "preview",
                    idempotency_key = "offline-preview",
                }
            );
            var b = await Call(
                client,
                "start_render",
                new
                {
                    project_id = projectId,
                    revision = 2,
                    quality = "preview",
                    idempotency_key = "offline-preview",
                }
            );
            Assert(a.Str("job_id") == b.Str("job_id"), "Duplicate job");
            previewJob = a.Str("job_id");
            Assert(a.Str("state") == "queued" && !a.Bool("worker_alive"), "Missing worker misreported");
            var e = await Call(
                client,
                "start_render",
                new
                {
                    project_id = projectId,
                    revision = 2,
                    quality = "final",
                    idempotency_key = "offline-preview",
                },
                true
            );
            Assert(e.Str("code") == "IDEMPOTENCY_CONFLICT", "Key conflict ignored");
        }
    );
    await Test(
        "A13",
        async () =>
        {
            var draft = project.DeepClone();
            draft["captions"]!["mode"] = "word";
            var p = await Call(client, "create_project", new { project = draft });
            var v = await Call(client, "validate_project", new { project_id = p.Str("project_id"), revision = 1 });
            Assert(
                v["errors"]!.AsArray().Any(e => e!.Str("code") == "ALIGNMENT_UNAVAILABLE"),
                "Word mode fabricated timing"
            );
        }
    );
    await Test(
        "A16",
        async () =>
        {
            var r = await Call(
                client,
                "register_asset",
                new { source_path = Path.Combine(root, "contracts/project.schema.json"), kind = "image" },
                true
            );
            Assert(r.Str("code") == "VALIDATION_ERROR", "Import allowlist bypass");
            var bad = Path.Combine(root, "examples/offline-dialogue/not-media.png");
            await File.WriteAllTextAsync(bad, "<script>bad</script>");
            try
            {
                await Call(client, "register_asset", new { source_path = bad, kind = "image" }, true);
            }
            finally
            {
                File.Delete(bad);
            }
        }
    );
    var link = Path.Combine(root, "examples/acceptance-link");
    if (Directory.Exists(link))
    {
        await Test(
            "A16-symlink",
            async () =>
            {
                var error = await Call(
                    client,
                    "register_asset",
                    new { source_path = Path.Combine(link, "project.schema.json"), kind = "image" },
                    true
                );
                Assert(
                    error.Str("message").Contains("Symlink/reparse"),
                    "Linked path not rejected at allowlist boundary"
                );
            }
        );
    }
    await Test(
        "A14",
        async () =>
        {
            var e = await Call(
                client,
                "start_render",
                new
                {
                    project_id = projectId,
                    revision = 2,
                    quality = "final",
                    idempotency_key = "limit",
                    allow_new_tts = true,
                    max_new_tts_characters = 4001,
                },
                true
            );
            Assert(e.Str("code") == "TTS_LIMIT_EXCEEDED", "Server cap ignored");
        }
    );
    await Test(
        "A14-estimate",
        () =>
        {
            var old = Environment.GetEnvironmentVariable("TOON_VOICE_ALIASES");
            try
            {
                Environment.SetEnvironmentVariable(
                    "TOON_VOICE_ALIASES",
                    "{\"fixture\":{\"provider\":\"elevenlabs\",\"voice_id\":\"fixture\",\"model_id\":\"fixture\"}}"
                );
                var draft = project.DeepClone();
                foreach (var u in draft.Items("scenes").SelectMany(s => s.Items("utterances")))
                {
                    u["audio"] = J.Node(
                        new
                        {
                            mode = "tts",
                            voice_alias = "fixture",
                            delivery = "neutral",
                        }
                    );
                }
                var saved = studio.Create(draft);
                var validation = studio.Validate(saved.Str("project_id"), 1);
                Assert(validation.Int("estimated_new_tts_characters") > 1, "No TTS estimate");
                Expect("TTS_REQUIRED", () => studio.Start(saved.Str("project_id"), 1, "preview", "no-paid", false, 0));
                Expect(
                    "TTS_LIMIT_EXCEEDED",
                    () => studio.Start(saved.Str("project_id"), 1, "preview", "too-small", true, 1)
                );
            }
            finally
            {
                Environment.SetEnvironmentVariable("TOON_VOICE_ALIASES", old);
            }

            return Task.CompletedTask;
        }
    );
}
await using (var client = await Connect())
    await Test(
        "A04",
        async () =>
        {
            var saved = await Call(client, "get_project", new { project_id = projectId });
            Assert(saved.Int("revision") == 2, "Revision lost across MCP sessions");
            await client.ReadResourceAsync($"toon://projects/{projectId}/revisions/1");
        }
    );
var prepared = new Dictionary<string, AudioInfo>();
foreach (var u in project.Items("scenes").SelectMany(s => s.Items("utterances")))
{
    var a = studio.Store.Asset(u["audio"]!["asset"]!);
    prepared[u.Str("utterance_id")] = await new Media(config).Prepare(
        studio.Assets.Resolve(u["audio"]!["asset"]!),
        a["alignment"],
        CancellationToken.None
    );
}
await Test(
    "A12",
    () =>
    {
        var tooLong = prepared.ToDictionary(x => x.Key, x => x.Value with { Samples = 48000 * 20 });
        Expect("DURATION_LIMIT_EXCEEDED", () => Compiler.Compile(project, projectId, 2, tooLong));
        Assert(J.Frame(50) == 2, "Midpoint rounding incorrect");

        return Task.CompletedTask;
    }
);
await Test(
    "final-frame",
    () =>
    {
        var timeline = Compiler.Compile(project, projectId, 2, prepared);
        Assert(
            timeline["scenes"]!.AsArray().Last()!["overlays"]![0]!.Int("end_frame_exclusive")
                == timeline.Int("total_frames"),
            "Final CTA must remain visible through final ceiling frame"
        );

        return Task.CompletedTask;
    }
);
await Test(
    "A07",
    () =>
    {
        var cameraEdit = project.DeepClone();
        cameraEdit["scenes"]![0]!["camera"] = J.Node(new { shot = "wide" });
        var a = Compiler.Compile(project, projectId, 2, prepared);
        var b = Compiler.Compile(cameraEdit, projectId, 3, prepared);
        Assert(J.Canonical(a["utterances"]!) == J.Canonical(b["utterances"]!), "Visual edit changed audio");

        return Task.CompletedTask;
    }
);
await Test(
    "A08",
    () =>
    {
        var voice = J.Node(
            new
            {
                provider = "elevenlabs",
                voice_id = "fixture_voice",
                model_id = "fixture_model",
                delivery_applied = "neutral",
                adapter_version = "1.0.0",
            }
        );
        var before = project
            .Items("scenes")
            .SelectMany(s => s.Items("utterances"))
            .Select(u => Speech.CacheKey(u.Str("text"), voice))
            .ToArray();
        var edit = project.DeepClone();
        edit["scenes"]![0]!["utterances"]![0]!["text"] = "Другая реплика.";
        var after = edit.Items("scenes")
            .SelectMany(s => s.Items("utterances"))
            .Select(u => Speech.CacheKey(u.Str("text"), voice))
            .ToArray();
        Assert(
            before.Zip(after).Count(pair => pair.First != pair.Second) == 1,
            "Cache invalidation extends beyond changed utterance"
        );

        return Task.CompletedTask;
    }
);
await Test(
    "alignment",
    () =>
    {
        var input = J.Node(
            new
            {
                characters = new[] { "Д", "а", ".", " ", "Д", "а", "!" },
                character_start_times_seconds = new[] { 0, .05, .1, .15, .2, .25, .3 },
                character_end_times_seconds = new[] { .05, .1, .15, .2, .25, .3, .35 },
            }
        );
        var a = Speech.ConvertAlignment(input, "Да. Да!", 400)!;
        Assert(a["normalized_words"]!.AsArray().Count == 2, "Character-to-word alignment failed");
        Expect("ALIGNMENT_UNAVAILABLE", () => Speech.ConvertAlignment(input, "2 квартиры", 400));
        input["character_end_times_seconds"]![6] = .6;
        var punctuation = Speech.ConvertAlignment(input, "Да. Да!", 400)!;
        Assert(
            punctuation["normalized_words"]![1]!.Num("end_ms") == 300,
            "Trailing punctuation must not extend a spoken word beyond its actual provider letter timing"
        );
        Assert(punctuation["normalized_words"]![1]!.Str("text") == "Да!", "Punctuation disappeared from display");
        input["character_end_times_seconds"]![5] = .6;
        Expect("VALIDATION_ERROR", () => Speech.ConvertAlignment(input, "Да. Да!", 400));
        var fallback = Speech.ReadAlignment(input, "Да. Да!", 400);
        Assert(
            fallback.Alignment == null && fallback.Warning == "VALIDATION_ERROR",
            "Invalid provider timing must be explicitly unavailable, never fabricated"
        );

        return Task.CompletedTask;
    }
);
await Test(
    "A15",
    async () =>
    {
        var originalKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", "offline-fixture-not-a-real-key");
        try
        {
            var request = J.Node(
                new
                {
                    project_id = projectId,
                    revision = 2,
                    quality = "preview",
                    idempotency_key = "timeout-fixture",
                }
            );
            var job = studio.Store.Enqueue(request, new JsonObject());
            studio.Store.Cancel(job.Str("job_id"));
            var handler = new TimeoutFixture();
            using var http = new HttpClient(handler);
            var speech = new Speech(config, studio.Store, http);
            var u = J.Node(new { text = "Проверка неоднозначного ответа." });
            var voice = J.Node(
                new
                {
                    provider = "elevenlabs",
                    voice_id = "fixture",
                    model_id = "fixture-model",
                    voice_settings = new JsonObject(),
                    pronunciation_dictionary_locators = new JsonArray(),
                    adapter_version = "1.0.0",
                }
            );
            for (var i = 0; i < 2; i++)
            {
                try
                {
                    await speech.Get(u, voice, job.Str("job_id"), CancellationToken.None);
                    throw new Exception("Timeout accepted");
                }
                catch (ToonException e)
                {
                    Assert(e.Code == "PROVIDER_OUTCOME_UNKNOWN", "Unexpected error " + e.Code);
                }
            }

            Assert(handler.Calls == 1, "Ambiguous request was sent twice");
            Assert(
                studio.Store.Job(job.Str("job_id")).Items("provider_usage_unknown").Count() == 1,
                "Unknown usage missing"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", originalKey);
        }
    }
);
await Test(
    "provider-cache",
    async () =>
    {
        var originalKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", "offline-fixture-not-a-real-key");
        try
        {
            var wav = prepared.Values.First().Path;
            var mp3 = Path.Combine(testRoot, "provider-fixture.mp3");
            await Processes.Run(
                config.Ffmpeg,
                ["-v", "error", "-y", "-i", wav, "-c:a", "libmp3lame", mp3],
                root,
                CancellationToken.None
            );
            var handler = new SuccessFixture(await File.ReadAllBytesAsync(mp3));
            using var http = new HttpClient(handler);
            var speech = new Speech(config, studio.Store, http);
            var request = J.Node(
                new
                {
                    project_id = projectId,
                    revision = 2,
                    quality = "preview",
                    idempotency_key = "success-fixture",
                }
            );
            var job = studio.Store.Enqueue(request, new JsonObject());
            studio.Store.Cancel(job.Str("job_id"));
            var u = J.Node(new { text = "Опять весь вечер проверял сайты." });
            var voice = J.Node(
                new
                {
                    provider = "elevenlabs",
                    voice_id = "fixture",
                    model_id = "fixture-model",
                    voice_settings = new JsonObject(),
                    pronunciation_dictionary_locators = new JsonArray(),
                    adapter_version = "1.0.0",
                }
            );
            var a = await speech.Get(u, voice, job.Str("job_id"), CancellationToken.None);
            var b = await speech.Get(u, voice, job.Str("job_id"), CancellationToken.None);
            Assert(handler.Calls == 1 && a.Hash == b.Hash, "Cache failed");
            Assert(
                studio.Store.Job(job.Str("job_id")).Items("provider_usage_confirmed").Count() == 1,
                "Confirmed response missing"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", originalKey);
        }
    }
);
await Test(
    "srt-and-sidecar",
    async () =>
    {
        var sidecar = Path.Combine(root, "examples/offline-dialogue/acceptance.srt");
        await File.WriteAllTextAsync(sidecar, "1\n00:00:00,000 --> 00:00:01,000\nОпять весь вечер проверял сайты.\n");
        try
        {
            var a = await studio.Assets.Register(
                Path.Combine(root, "examples/offline-dialogue/problem.wav"),
                "audio",
                sidecar,
                CancellationToken.None
            );
            Assert(a["alignment"]!["normalized_words"] == null, "SRT incorrectly advertised word timing");
            var p = project.DeepClone();
            p["scenes"]![0]!["utterances"]![0]!["audio"]!["asset"] = J.Node(
                new { asset_id = a.Str("asset_id"), version = a.Str("version") }
            );
            var preparedSrt = prepared.ToDictionary(x => x.Key, x => x.Value);
            preparedSrt["problem"] = preparedSrt["problem"] with { Alignment = a["alignment"] };
            Compiler.Compile(p, projectId, 2, preparedSrt);
        }
        finally
        {
            File.Delete(sidecar);
        }

        var illustration = J.Read(Path.Combine(root, "tests/fixtures/recorded-line.alignment.json"));
        Expect("ALIGNMENT_UNAVAILABLE", () => Validation.Alignment(illustration, illustration.Num("duration_ms")));
    }
);
await Test(
    "configured-import-limit",
    async () =>
    {
        var old = Environment.GetEnvironmentVariable("TOON_MAX_MEDIA_BYTES");
        try
        {
            Environment.SetEnvironmentVariable("TOON_MAX_MEDIA_BYTES", "1");
            try
            {
                await studio.Assets.Register(
                    Path.Combine(root, "examples/offline-dialogue/problem.wav"),
                    "audio",
                    null,
                    CancellationToken.None
                );
                throw new Exception("Configured media limit ignored");
            }
            catch (ToonException e)
            {
                Assert(e.Code == "VALIDATION_ERROR", "Unexpected import error");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOON_MAX_MEDIA_BYTES", old);
        }
    }
);

if (args.Contains("--lifecycle"))
{
    studio.Store.Cancel(previewJob);
    var launch = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = root,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
    };
    launch.ArgumentList.Add(Path.Combine(root, "src/Toon.Worker/bin/Debug/net10.0/Toon.Worker.dll"));
    using var worker = Process.Start(launch)!;
    var stderr = worker.StandardError.ReadToEndAsync();
    var stdout = worker.StandardOutput.ReadToEndAsync();
    try
    {
        var cancel = studio.Start(projectId, 2, "preview", "focused-cancel", false, 0);
        var pid = await RenderPid(cancel.Str("job_id"));
        studio.Store.Cancel(cancel.Str("job_id"));
        await Wait(cancel.Str("job_id"), "cancelled");
        Assert(Exited(pid) && !worker.HasExited, "Cancellation did not isolate worker/process tree");
        Pass("A11", "Real renderer PID terminated; worker remained alive");
        var crash = studio.Start(projectId, 2, "preview", "focused-crash", false, 0);
        pid = await RenderPid(crash.Str("job_id"));
        worker.Kill(false);
        await worker.WaitForExitAsync();
        await Task.Delay(11000);
        var recovered = studio.Job(crash.Str("job_id"));
        Assert(
            Exited(pid)
                && recovered.Str("state") == "failed"
                && recovered["error"]!.Str("code") == "WORKER_INTERRUPTED",
            "Crash recovery failed"
        );
        Pass("A10", "Kernel process-tree cleanup and expired lease verified");
    }
    finally
    {
        if (!worker.HasExited)
        {
            worker.Kill(true);
        }
        await worker.WaitForExitAsync();
        await File.WriteAllTextAsync(Path.Combine(testRoot, "worker.log"), await stderr + await stdout);
    }

    J.Write(Path.Combine(testRoot, "acceptance.json"), J.Node(new { mode = "focused-lifecycle", results }));
    Console.WriteLine("REPORT " + Path.Combine(testRoot, "acceptance.json"));
    Environment.ExitCode = errors == 0 ? 0 : 1;

    return;
}

if (!args.Contains("--quick"))
{
    var psi = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = root,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
    };
    psi.ArgumentList.Add(Path.Combine(root, "src/Toon.Worker/bin/Debug/net10.0/Toon.Worker.dll"));
    using var worker = Process.Start(psi)!;
    var workerLog = worker.StandardError.ReadToEndAsync();
    var workerOut = worker.StandardOutput.ReadToEndAsync();
    try
    {
        await Test(
            "A09",
            async () =>
            {
                await Wait(previewJob, "render");
                await using (var disconnected = await Connect())
                {
                    await Call(disconnected, "get_job", new { job_id = previewJob });
                }
                Assert(!worker.HasExited, "MCP shutdown killed worker");
            }
        );
        await Test(
            "A02",
            async () =>
            {
                var job = await Wait(previewJob, "succeeded");
                Assert(job.Items("artifacts").Any(a => a.Str("name") == "video.mp4"), "No verified MP4");
            }
        );
        await using (var client = await Connect())
        {
            var final = await Call(
                client,
                "start_render",
                new
                {
                    project_id = projectId,
                    revision = 2,
                    quality = "final",
                    idempotency_key = "offline-final",
                }
            );
            finalJob = final.Str("job_id");
            await Test(
                "A18",
                async () =>
                {
                    var job = await Wait(finalJob, "succeeded");
                    var artifacts = await Call(client, "get_artifacts", new { job_id = finalJob });
                    Assert(artifacts.AsArray().Count == 8, "Missing final deliverables");
                    var report = J.Read(
                        job.Items("artifacts").Single(a => a.Str("name") == "validation-report.json").Str("local_path")
                    );
                    Assert(report.Bool("full_decode"), "Not fully decoded");
                }
            );
            await Test(
                "A19",
                () =>
                {
                    var a = J.Read(Path.Combine(testRoot, "jobs", previewJob, "timeline.json"));
                    var b = J.Read(Path.Combine(testRoot, "jobs", finalJob, "timeline.json"));
                    Assert(J.Canonical(a) == J.Canonical(b), "Preview and final timelines differ");
                    var frames = Directory.GetFiles(Path.Combine(testRoot, "jobs", previewJob), "frame-*.png");
                    foreach (var f in frames)
                    {
                        Assert(
                            J.FileHash(f) == J.FileHash(Path.Combine(testRoot, "jobs", finalJob, Path.GetFileName(f))),
                            "Non-deterministic PNG " + Path.GetFileName(f)
                        );
                    }
                    return Task.CompletedTask;
                }
            );
            await Test(
                "A11",
                async () =>
                {
                    var queued = await Call(
                        client,
                        "start_render",
                        new
                        {
                            project_id = projectId,
                            revision = 2,
                            quality = "final",
                            idempotency_key = "cancel-during-render",
                        }
                    );
                    var id = queued.Str("job_id");
                    var pid = await RenderPid(id);
                    await Call(client, "cancel_job", new { job_id = id });
                    var cancelled = await Wait(id, "cancelled");
                    Assert(!cancelled.Items("artifacts").Any(), "Cancelled artifacts leaked");
                    Assert(Exited(pid), "Cancelled renderer is still alive");
                    Assert(!worker.HasExited, "Cancel stopped persistent worker");
                }
            );
            await Test(
                "A10",
                async () =>
                {
                    var queued = await Call(
                        client,
                        "start_render",
                        new
                        {
                            project_id = projectId,
                            revision = 2,
                            quality = "final",
                            idempotency_key = "crash-during-render",
                        }
                    );
                    var id = queued.Str("job_id");
                    var pid = await RenderPid(id);
                    worker.Kill(false);
                    await worker.WaitForExitAsync();
                    await Task.Delay(11000);
                    Assert(Exited(pid), "Renderer survived worker crash");
                    var recovered = await Call(client, "get_job", new { job_id = id });
                    Assert(
                        recovered.Str("state") == "failed"
                            && recovered["error"]!.Str("code") == "WORKER_INTERRUPTED"
                            && !recovered.Items("artifacts").Any(),
                        "Crash recovery invalid"
                    );
                }
            );
        }
    }
    finally
    {
        if (!worker.HasExited)
        {
            worker.Kill(true);
        }
        await worker.WaitForExitAsync();
        await File.WriteAllTextAsync(Path.Combine(testRoot, "worker.log"), await workerLog + await workerOut);
    }
}
foreach (
    var (id, detail) in new[]
    {
        ("A20", "Real Novostroykin screenshot not supplied; synthetic exact-image fixture only"),
        ("A21", "No authorized live TTS budget/voice selection; no paid request sent"),
        (
            "manual-listening",
            "Requires listening to actual 27.7-second speech output; synthetic fixture uses local Irina presets"
        ),
        ("linux-wsl", "This run uses Windows; target Linux/WSL acceptance pending"),
    }
)
{
    results.Add(
        J.Node(
            new
            {
                id,
                status = "pending",
                detail,
            }
        )
    );
}
J.Write(
    Path.Combine(testRoot, "acceptance.json"),
    J.Node(
        new
        {
            project_id = projectId,
            preview_job = previewJob,
            final_job = finalJob,
            results,
        }
    )
);
Console.WriteLine("REPORT " + Path.Combine(testRoot, "acceptance.json"));
Environment.ExitCode = errors == 0 ? 0 : 1;

async Task<JsonNode> Wait(string id, string target)
{
    var watch = Stopwatch.StartNew();
    while (watch.Elapsed < TimeSpan.FromMinutes(20))
    {
        var j = studio.Store.Job(id);
        if (j.Str("state") == target || j["stage"]?.ToString() == target)
            return j;

        if (j.Str("state") is "failed" or "cancelled" or "succeeded")
            throw new Exception("Unexpected terminal job: " + j.Str("state") + " " + j["error"]);

        await Task.Delay(500);
    }

    throw new TimeoutException("Job did not reach " + target);
}
async Task<int> RenderPid(string id)
{
    await Wait(id, "render");
    var file = Path.Combine(testRoot, "jobs", id, "renderer.log.process.json");
    var timeout = Stopwatch.StartNew();
    while (!File.Exists(file) && timeout.Elapsed < TimeSpan.FromSeconds(20))
    {
        await Task.Delay(100);
    }
    await Task.Delay(2000);
    var pid = J.Read(file).Int("pid");
    Assert(!Exited(pid), "Renderer did not start");

    return pid;
}
bool Exited(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);

        return process.HasExited;
    }
    catch (ArgumentException)
    {
        return true;
    }
}

sealed class TimeoutFixture : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        throw new HttpRequestException("Simulated disconnect after dispatch; controlled offline fixture");
    }
}

sealed class SuccessFixture(byte[] audio) : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;

        return Task.FromResult(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    J.Node(new { audio_base64 = Convert.ToBase64String(audio) }).ToJsonString(),
                    System.Text.Encoding.UTF8,
                    "application/json"
                ),
            }
        );
    }
}
