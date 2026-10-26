using System.Text.Json.Nodes;
using Toon.Core;
using Toon.Infrastructure;

namespace Toon.Acceptance;

public static class WorkflowAcceptance
{
    public static async Task Run(Config originalConfig)
    {
        var input = Path.Combine(originalConfig.Data, "input");
        Directory.CreateDirectory(input);
        Environment.SetEnvironmentVariable(
            "TOON_IMPORT_ROOTS",
            Path.Combine(originalConfig.Root, "examples") + Path.PathSeparator + input
        );
        Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", null);
        Environment.SetEnvironmentVariable("TOON_USE_USER_SECRETS", "0");
        Environment.SetEnvironmentVariable("TOON_ALLOW_VOICE_CLONING", "1");
        Environment.SetEnvironmentVariable("TOON_MAX_VOICE_CLONES", "1");
        var config = new Config();
        await using var session = new WorkflowSession(config);
        var toolNames = (await (await session.Connect()).ListToolsAsync()).Select(tool => tool.Name).ToHashSet();
        foreach (var kind in new[] { "character", "environment", "voice", "production" })
        {
            var workflow = await session.Call("get_content_workflow", new { kind });
            var resource = JsonNode.Parse(await session.Resource("toon://workflows/" + kind))!;
            session.Check(J.Canonical(workflow) == J.Canonical(resource), "workflow tool/resource agree: " + kind);
            session.Check(
                workflow.Items("tools").All(tool => toolNames.Contains(tool.GetValue<string>())),
                "all workflow tools discoverable: " + kind
            );
        }
        session.Check(
            (await session.Resource("toon://guides/content-workflow")).Contains(
                "get_content_workflow",
                StringComparison.Ordinal
            ),
            "complete guide readable over MCP"
        );
        await session.Call("get_content_workflow", new { kind = "../secrets" }, "VALIDATION_ERROR");

        async Task<JsonNode> Create(string kind, string name)
        {
            var template = await session.Call("get_submission_template", new { kind });
            session.Check(
                template
                    .Items("workflow")
                    .Select(x => x.GetValue<string>())
                    .SequenceEqual(ContentWorkflows.Tools(kind)),
                "submission contains complete workflow: " + kind
            );
            var submission = template["submission"]!.DeepClone();
            submission["display_name"] = name;
            submission["description"] =
                "Explicit original synthetic fixture for infrastructure acceptance; not actual user artwork or a real cloned voice";
            submission["rights_confirmed"] = true;
            if (kind == "voice")
            {
                submission["details"]!["cloning_consent"] = true;
                submission["details"]!["cloud_upload_consent"] = true;
            }
            return await session.Call("create_library_item", new { submission });
        }
        async Task<JsonNode> Import(JsonNode item, string role, string path) =>
            await session.Call(
                "import_library_material",
                new
                {
                    item_id = item.Str("item_id"),
                    expected_revision = item.Int("revision"),
                    role,
                    source_path = path,
                    note = "Synthetic fixture only; original pixels/audio preserved",
                }
            );
        async Task<JsonNode> Current(JsonNode item) =>
            await session.Call("get_library_item", new { item_id = item.Str("item_id") });
        async Task<JsonNode> Recipe(JsonNode item, string kind, JsonNode recipe) =>
            await session.Call(
                "set_" + kind + "_recipe",
                new
                {
                    item_id = item.Str("item_id"),
                    expected_revision = item.Int("revision"),
                    recipe,
                }
            );
        async Task<JsonNode> Prepare(JsonNode item, string key)
        {
            var args = new
            {
                item_id = item.Str("item_id"),
                expected_revision = item.Int("revision"),
                idempotency_key = key,
            };
            var job = await session.Call("prepare_library_item", args);
            session.Check(
                (await session.Call("prepare_library_item", args)).Str("job_id") == job.Str("job_id"),
                "preparation idempotent: " + key
            );
            return job;
        }
        async Task<JsonNode> Publish(JsonNode item, string version, string? error = null) =>
            await session.Call(
                "publish_library_item",
                new
                {
                    item_id = item.Str("item_id"),
                    expected_revision = item.Int("revision"),
                    version,
                    review_approved = true,
                    review_note = "Synthetic infrastructure test approval only; real artwork likeness and human review are separate",
                },
                error
            );

        var character = await Create("character", "Вася Пупкин — учебный макет");
        foreach (var role in new[] { "front", "three_quarter" })
        {
            character = await Import(character, role, Path.Combine(config.Root, "examples/character-cutout/sheet.png"));
        }
        var characterRecipe = await session.Call("get_character_recipe_template", new { });
        character = await Recipe(character, "character", characterRecipe);
        var characterPreparation = await Prepare(character, "character-prepare");

        var environment = await Create("environment", "Мастерская — учебная декорация");
        environment = await Import(
            environment,
            "wide",
            Path.Combine(config.Root, "examples/environment-workshop/wide.png")
        );
        var environmentRecipe = await session.Call("get_environment_recipe_template", new { });
        var badRecipe = environmentRecipe.DeepClone();
        badRecipe["environment"]!["layers"]![0]!["nodes"]![0]!["polygon"]![1]![0] = 3000;
        environment = await Recipe(environment, "environment", badRecipe);
        var badPreparation = await Prepare(environment, "environment-bad-bounds");

        var samplePath = Path.Combine(input, "voice-fixture.wav");
        await Processes.Run(
            config.Ffmpeg,
            [
                "-v",
                "error",
                "-y",
                "-stream_loop",
                "3",
                "-i",
                Path.Combine(config.Root, "examples/offline-dialogue/problem.wav"),
                samplePath,
            ],
            config.Root,
            CancellationToken.None
        );
        var voice = await Create("voice", "Локальная речевая fixture — не реальный клон");
        voice = await Import(voice, "sample", samplePath);
        var voicePreparation = await Prepare(voice, "voice-prepare");
        session.Check(
            characterPreparation.Str("state") == "queued" && voicePreparation.Str("state") == "queued",
            "preparation persists while worker is stopped"
        );
        await session.Disconnect();
        session.StartWorker();
        await session.Wait(characterPreparation.Str("job_id"));
        var failed = await session.Wait(badPreparation.Str("job_id"), "failed");
        session.Check(failed["error"]!.Str("code") == "VALIDATION_ERROR", "worker rejects measured material bounds");
        await session.Wait(voicePreparation.Str("job_id"));
        character = await Current(character);
        environment = await Current(environment);
        voice = await Current(voice);
        session.Check(environment.Str("state") == "draft", "failed preparation returns editable draft");
        environment = await Recipe(environment, "environment", environmentRecipe);
        var environmentPreparation = await Prepare(environment, "environment-fixed");
        await session.Wait(environmentPreparation.Str("job_id"));
        environment = await Current(environment);
        foreach (var item in new[] { character, environment, voice })
        {
            var materials = await session.Call(
                "get_library_materials",
                new { item_id = item.Str("item_id"), revision = item.Int("revision") }
            );
            session.Check(
                materials.AsArray().All(material => J.FileHash(material!.Str("local_path")) == material!.Str("sha256")),
                "original material hashes readable through MCP"
            );
            await session.Resource($"toon://library/{item.Str("item_id")}/revisions/{item.Int("revision")}/package");
        }
        await Publish(character, "1.0.0", "CHARACTER_TEST_REQUIRED");
        await Publish(environment, "1.0.0", "ENVIRONMENT_TEST_REQUIRED");
        var recording = await session.Call(
            "register_asset",
            new { source_path = Path.Combine(config.Root, "examples/offline-dialogue/problem.wav"), kind = "audio" }
        );
        var audioReference = J.Node(new { asset_id = recording.Str("asset_id"), version = recording.Str("version") });
        async Task<JsonNode> Trial(JsonNode item, string kind, string key) =>
            await session.Call(
                "start_" + kind + "_test",
                new
                {
                    item_id = item.Str("item_id"),
                    expected_revision = item.Int("revision"),
                    audio_asset = audioReference,
                    transcript = "Опять весь вечер проверял сайты.",
                    idempotency_key = key,
                }
            );
        var characterTest = await Trial(character, "character", "character-test");
        var environmentTest = await Trial(environment, "environment", "environment-test");
        await session.Disconnect();
        await session.Wait(characterTest.Str("job_id"));
        await session.Wait(environmentTest.Str("job_id"));
        foreach (var test in new[] { characterTest, environmentTest })
        {
            var artifacts = await session.Call("get_artifacts", new { job_id = test.Str("job_id") });
            session.Check(
                artifacts.AsArray().Any(a => a!.Str("name") == "video.mp4"),
                "verified trial MP4 available over MCP"
            );
        }
        character = await Publish(character, "1.0.0");
        environment = await Publish(environment, "1.0.0");
        session.Check(
            character.Bool("render_supported") && environment.Bool("render_supported"),
            "new character and scenery published through MCP"
        );
        var characterId = character.Str("item_id");
        var environmentId = environment.Str("item_id");
        var oldCharacter = J.Canonical(
            await session.Call("get_library_item", new { item_id = characterId, version = "1.0.0" })
        );
        var oldEnvironment = J.Canonical(
            await session.Call("get_library_item", new { item_id = environmentId, version = "1.0.0" })
        );

        await session.StopWorker();
        var baseProject = J.Read(Path.Combine(config.Root, "examples/offline-dialogue/project.json"));
        var speechByText = new Dictionary<string, byte[]>();
        foreach (var line in baseProject.Items("scenes").SelectMany(scene => scene.Items("utterances")))
        {
            var assetId = line["audio"]!["asset"]!.Str("asset_id");
            var mp3 = Path.Combine(input, assetId + ".mp3");
            await Processes.Run(
                config.Ffmpeg,
                [
                    "-v",
                    "error",
                    "-y",
                    "-i",
                    Path.Combine(config.Root, "examples/offline-dialogue", assetId + ".wav"),
                    "-c:a",
                    "libmp3lame",
                    mp3,
                ],
                config.Root,
                CancellationToken.None
            );
            speechByText[J.Normalize(line.Str("text"))] = await File.ReadAllBytesAsync(mp3);
        }
        using var transport = new VoiceAcceptance.VoiceTransport(
            speechByText.First().Value,
            await File.ReadAllBytesAsync(samplePath),
            speechByText
        );
        using var http = new HttpClient(transport);
        var fixtureStudio = new Studio(config, http);
        async Task<JsonNode> ExecuteFixture(JsonNode job)
        {
            Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", "offline-fixture-not-a-secret");
            try
            {
                var claimed = fixtureStudio.Store.Claim()!;
                session.Check(
                    claimed.Str("job_id") == job.Str("job_id"),
                    "injected fixture worker claims expected voice job"
                );
                await new Worker(fixtureStudio).Execute(claimed, CancellationToken.None);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", null);
            }
            return await session.Wait(job.Str("job_id"));
        }
        var cloneArgs = new
        {
            item_id = voice.Str("item_id"),
            expected_revision = voice.Int("revision"),
            allow_cloud_upload = true,
            max_clone_requests = 1,
            sample_reviewed = true,
            review_note = "Synthetic offline fixture with injected HTTP only",
            idempotency_key = "workflow-clone",
        };
        var clone = await session.Call("start_voice_clone", cloneArgs);
        session.Check(
            (await session.Call("start_voice_clone", cloneArgs)).Str("job_id") == clone.Str("job_id"),
            "clone request idempotent over MCP"
        );
        clone = await ExecuteFixture(clone);
        var profile = clone["result"]!["profile"]!;
        var unconfigured = profile.DeepClone();
        var requestsBeforeConfiguration = transport.Requests;
        profile = await session.Call(
            "configure_voice_profile",
            new
            {
                profile_id = profile.Str("profile_id"),
                expected_revision = profile.Int("revision"),
                voice_settings = new VoiceSettings
                {
                    Stability = .5,
                    SimilarityBoost = .75,
                    Style = 0,
                    UseSpeakerBoost = true,
                    Speed = 1,
                },
                note = "Offline delivery configuration fixture",
            }
        );
        session.Check(
            profile.Int("revision") == unconfigured.Int("revision") + 1
                && profile.Str("state") == "pending_review"
                && profile["voice_settings"]!["speed"]!.GetValue<double>() == 1
                && transport.Requests == requestsBeforeConfiguration,
            "MCP voice configuration creates a revision without cloud requests"
        );
        foreach (var text in speechByText.Keys)
        {
            var preview = await session.Call(
                "start_voice_preview",
                new
                {
                    profile_id = profile.Str("profile_id"),
                    expected_revision = profile.Int("revision"),
                    text,
                    idempotency_key = "preview-" + J.Hash(text),
                    allow_new_tts = true,
                    max_new_tts_characters = 240,
                }
            );
            await ExecuteFixture(preview);
        }
        profile = await session.Call(
            "review_voice_profile",
            new
            {
                profile_id = profile.Str("profile_id"),
                expected_revision = profile.Int("revision"),
                approved = true,
                review_note = "Infrastructure fixture approval only; not a real cloned voice or similarity review",
            }
        );
        session.Check(
            J.Canonical(
                JsonNode.Parse(
                    await session.Resource(
                        $"toon://voices/{profile.Str("profile_id")}/revisions/{profile.Int("revision")}"
                    )
                )!
            ) == J.Canonical(profile),
            "immutable voice revision resource matches tool"
        );
        foreach (var id in new[] { characterId, "advisor" })
        {
            await session.Call(
                "bind_character_voice",
                new
                {
                    character_id = id,
                    version = "1.0.0",
                    expected_binding_revision = 0,
                    profile_id = profile.Str("profile_id"),
                    profile_revision = profile.Int("revision"),
                }
            );
            var binding = await session.Call("get_character_voice", new { character_id = id, version = "1.0.0" });
            session.Check(
                binding["voice_profile"]!.Str("profile_id") == profile.Str("profile_id"),
                "voice binding readable over MCP: " + id
            );
        }

        var project = JsonNode.Parse(baseProject.ToJsonString().Replace("\"buyer\"", "\"" + characterId + "\""))!;
        project["title"] = "Вася в мастерской — сквозная offline fixture";
        project["scenes"]![0]!["overlays"] = new JsonArray();
        foreach (var scene in project.Items("scenes"))
        {
            if (scene.Str("kind") == "dialogue")
            {
                scene["background"] = J.Node(new { asset_id = environmentId, version = "1.0.0" });
            }
            foreach (var line in scene.Items("utterances"))
            {
                line["audio"] = J.Node(
                    new
                    {
                        mode = "tts",
                        voice_alias = "character",
                        delivery = "neutral",
                    }
                );
            }
        }
        var screen = await session.Call(
            "register_asset",
            new { source_path = Path.Combine(config.Root, "examples/offline-dialogue/screen.png"), kind = "image" }
        );
        project["scenes"]![1]!["overlays"]![0]!["content"]!["asset"] = J.Node(
            new { asset_id = screen.Str("asset_id"), version = screen.Str("version") }
        );
        var saved = await session.Call("create_project", new { project });
        var savedId = saved.Str("project_id");
        var savedProject = await session.Call("get_project", new { project_id = savedId, revision = 1 });
        var pinned = J.Canonical(savedProject["project"]!);
        session.Check(
            savedProject["project"]!
                .Items("characters")
                .All(c => c["voice_profile"]!.Int("revision") == profile.Int("revision")),
            "new project pins character voice revisions"
        );
        var readiness = await session.Call("validate_project", new { project_id = savedId, revision = 1 });
        session.Check(
            readiness.Bool("render_ready") && readiness.Int("estimated_new_tts_characters") == 0,
            "new character and scenery ready with fully cached speech"
        );
        async Task<JsonNode> Render(string key) =>
            await session.Call(
                "start_render",
                new
                {
                    project_id = savedId,
                    revision = 1,
                    quality = "final",
                    idempotency_key = key,
                    allow_new_tts = false,
                    max_new_tts_characters = 0,
                }
            );
        var cancelled = await Render("cancel-before-start");
        await session.Call("cancel_job", new { job_id = cancelled.Str("job_id") });
        session.Check(
            (await session.Wait(cancelled.Str("job_id"), "cancelled")).Items("artifacts").Count() == 0,
            "cancelled queued render publishes no artifacts"
        );
        var first = await Render("first-final");
        session.Check(
            (await Render("first-final")).Str("job_id") == first.Str("job_id"),
            "final render idempotent over MCP"
        );
        await session.Disconnect();
        session.StartWorker();
        await session.Wait(first.Str("job_id"));
        var firstArtifacts = await session.Call("get_artifacts", new { job_id = first.Str("job_id") });
        session.Check(
            firstArtifacts.AsArray().Any(a => a!.Str("name") == "video.mp4"),
            "new character scenery and bound voice produce verified MP4 after MCP disconnect"
        );

        var changedSubmission = character["submission"]!.DeepClone();
        changedSubmission["description"] = "Second immutable fixture release for version compatibility check";
        character = await session.Call(
            "update_library_item",
            new
            {
                item_id = characterId,
                expected_revision = character.Int("revision"),
                submission = changedSubmission,
            }
        );
        character = await Recipe(character, "character", characterRecipe);
        var nextPreparation = await Prepare(character, "character-v2");
        await session.Wait(nextPreparation.Str("job_id"));
        character = await Current(character);
        await Publish(character, "2.0.0", "CHARACTER_TEST_REQUIRED");
        var nextTrial = await Trial(character, "character", "character-v2-test");
        await session.Wait(nextTrial.Str("job_id"));
        character = await Publish(character, "2.0.0");
        var versions = await session.Call("list_library_versions", new { item_id = characterId });
        session.Check(versions.AsArray().Count == 2, "two immutable character releases available through MCP");
        session.Check(
            J.Canonical(await session.Call("get_library_item", new { item_id = characterId, version = "1.0.0" }))
                == oldCharacter,
            "old character release unchanged after publishing v2"
        );
        session.Check(
            J.Canonical(await session.Call("get_library_item", new { item_id = environmentId, version = "1.0.0" }))
                == oldEnvironment,
            "environment release unchanged"
        );
        session.Check(
            J.Canonical((await session.Call("get_project", new { project_id = savedId, revision = 1 }))["project"]!)
                == pinned,
            "saved project unchanged after content update"
        );

        var interrupted = await Render("interrupted-render");
        await session.Wait(interrupted.Str("job_id"), "running", "render");
        await session.StopWorker();
        await session.Disconnect();
        session.StartWorker();
        var recovered = await session.Wait(interrupted.Str("job_id"), "failed");
        session.Check(
            recovered["error"]!.Str("code") == "WORKER_INTERRUPTED" && !recovered.Items("artifacts").Any(),
            "real worker restart marks expired job failed without partial publication"
        );
        var final = await Render("retry-after-restart");
        await session.Disconnect();
        await session.Wait(final.Str("job_id"));
        var finalArtifacts = await session.Call("get_artifacts", new { job_id = final.Str("job_id") });
        Dictionary<string, string> Frames(string jobId) =>
            Directory
                .GetFiles(Path.Combine(config.Data, "jobs", jobId), "frame-*.png")
                .ToDictionary(
                    path => Path.GetFileName(path),
                    path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
                );
        var firstFrames = Frames(first.Str("job_id"));
        var finalFrames = Frames(final.Str("job_id"));
        session.Check(
            firstFrames.Count > 10
                && firstFrames.Count == finalFrames.Count
                && firstFrames.All(frame => finalFrames.TryGetValue(frame.Key, out var hash) && hash == frame.Value),
            "old project control frames reproduce exactly after content update and worker restart"
        );
        var finalDirectory = Path.Combine(config.Data, "artifacts", final.Str("job_id"));
        var validation = J.Read(Path.Combine(finalDirectory, "validation-report.json"));
        session.Check(
            validation.Bool("verified")
                && validation.Int("audio_cache_hits") == speechByText.Count
                && validation.Int("new_tts_characters_estimate") == 0,
            "final verification confirms cached audio and no new synthesis"
        );
        session.Check(
            transport.Clones == 1 && transport.Syntheses == speechByText.Count && transport.MultipartValid,
            "one injected clone and exactly one fixture synthesis per line; no cloud requests"
        );
        await session.Call("get_catalog", new { kind = "characters" });
        await session.Call("get_catalog", new { kind = "backgrounds" });
        await session.Call("list_voice_profiles", new { });
        await session.Resource($"toon://projects/{savedId}/revisions/1");
        await session.Resource($"toon://jobs/{final.Str("job_id")}/report");
        J.Write(
            Path.Combine(config.Data, "workflow-acceptance.json"),
            J.Node(
                new
                {
                    checks = session.Checks,
                    tools_called = session.CalledTools.Order().ToArray(),
                    character_id = characterId,
                    environment_id = environmentId,
                    profile_id = profile.Str("profile_id"),
                    project_id = savedId,
                    final_job = final.Str("job_id"),
                    first_final_job = first.Str("job_id"),
                    character_test_job = characterTest.Str("job_id"),
                    environment_test_job = environmentTest.Str("job_id"),
                    provider_transport = "Injected offline HTTP fixture; independent render worker has no API credentials",
                    cloud_requests = 0,
                    human_review = "pending final visual/listening review; fixture library approvals are infrastructure checks only",
                    actual_user_sketches = "pending",
                    live_clone = "pending actual sample, consent and separate authorization",
                }
            )
        );
        Console.WriteLine("REPORT " + Path.Combine(config.Data, "workflow-acceptance.json"));
    }
}
