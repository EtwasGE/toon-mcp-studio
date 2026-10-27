using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class Studio
{
    public Config Config { get; }
    public Store Store { get; }
    public Assets Assets { get; }
    public Speech Speech { get; }
    public ContentLibrary Library { get; }
    public VoiceService Voices { get; }
    public SoundEffects Sounds { get; }
    public PropLibrary Props { get; }

    public Studio(Config config, HttpClient? voiceClient = null)
    {
        Config = config;
        Store = new Store(config);
        Assets = new Assets(config, Store);
        Speech = new Speech(config, Store, voiceClient);
        Library = new ContentLibrary(config, Store, Assets);
        Voices = new VoiceService(this, voiceClient);
        Sounds = new SoundEffects(this, voiceClient);
        Props = new PropLibrary(this);
    }

    public void CheckDraft(JsonNode project, JsonNode? resolvedRigs = null, JsonNode? resolvedEnvironments = null)
    {
        Validation.Schema(project, Path.Combine(Config.Root, "contracts/project.schema.json"));
        Validation.Semantic(
            project,
            resolvedRigs ?? Library.ResolveCharacters(project),
            resolvedEnvironments ?? Library.ResolveEnvironments(project)
        );
    }

    public JsonNode Create(JsonNode project)
    {
        project = Props.Pin(Voices.Pin(project));
        CheckDraft(project);
        var result = Store.SaveProject(project);
        result["readiness"] = Validate(result.Str("project_id"), result.Int("revision"));

        return result;
    }

    public JsonNode Update(string id, int expected, JsonNode project)
    {
        project = Props.Pin(Voices.Pin(project));
        J.Require(expected > 0, "VALIDATION_ERROR", "expected_revision must be positive");
        CheckDraft(project);
        var result = Store.SaveProject(project, id, expected);
        result["readiness"] = Validate(id, result.Int("revision"));

        return result;
    }

    public JsonNode GetProject(string id, int? revision)
    {
        var result = Store.Project(id, revision);
        result["readiness"] = Validate(id, result.Int("revision"));

        return result;
    }

    public JsonNode Validate(string id, int revision)
    {
        var saved = Store.Project(id, revision);
        var p = saved["project"]!;
        var errors = new JsonArray();
        var warnings = new JsonArray();
        var resolvedAssets = new JsonObject();
        var resolvedRigs = new JsonObject();
        var resolvedEnvironments = new JsonObject();
        var voices = new JsonObject();
        var keys = new HashSet<string>();
        var chars = 0;
        try
        {
            CheckDraft(p);
        }
        catch (ToonException ex)
        {
            errors.Add(J.Node(new Issue(ex.Code, ex.Message, ex.Pointer)));
        }

        void Asset(JsonNode r, string pointer)
        {
            try
            {
                var a = Store.Asset(r);
                _ = Assets.Resolve(r);
                resolvedAssets[r.Str("asset_id") + "@" + r.Str("version")] = a;
            }
            catch (ToonException ex)
            {
                errors.Add(J.Node(new Issue(ex.Code, ex.Message, pointer)));
            }
        }

        try
        {
            resolvedEnvironments = Library.ResolveEnvironments(p);
            foreach (var environment in resolvedEnvironments.Select(pair => pair.Value!))
            {
                foreach (var node in Environments.Nodes(environment).Where(n => n.Str("type") == "cutout"))
                {
                    Asset(node["asset"]!, "/scenes/background");
                }
            }
        }
        catch (ToonException ex)
        {
            errors.Add(J.Node(new Issue(ex.Code, ex.Message, "/scenes")));
        }

        try
        {
            resolvedRigs = Library.ResolveCharacters(p);
            foreach (var rig in resolvedRigs.Select(pair => pair.Value!))
            {
                foreach (var node in CharacterRigs.Nodes(rig).Where(n => n.Str("type") == "cutout"))
                {
                    Asset(node["asset"]!, "/characters");
                }
            }
        }
        catch (ToonException ex)
        {
            errors.Add(J.Node(new Issue(ex.Code, ex.Message, "/characters")));
        }

        var si = 0;
        foreach (var s in p.Items("scenes"))
        {
            var ui = 0;
            foreach (var u in s.Items("utterances"))
            {
                var pointer = $"/scenes/{si}/utterances/{ui++}/audio";
                if (u["audio"]!.Str("mode") == "recording")
                {
                    var r = u["audio"]!["asset"]!;
                    Asset(r, pointer + "/asset");
                    if (resolvedAssets[r.Str("asset_id") + "@" + r.Str("version")] is JsonNode a)
                    {
                        if (a.Str("kind") != "audio")
                        {
                            errors.Add(J.Node(new Issue("VALIDATION_ERROR", "Recording asset must be audio", pointer)));
                        }
                        var wordNeeded =
                            p["captions"]!.Bool("enabled") && p["captions"]!.Str("mode") == "word"
                            || s.Items("events")
                                .Select(e => e["at"]!)
                                .Concat(s.Items("overlays").SelectMany(o => new[] { o["start"]!, o["end"]! }))
                                .Any(anchor =>
                                    anchor["utterance_id"]?.ToString() == u.Str("utterance_id")
                                    && anchor.Str("kind").StartsWith("word_", StringComparison.Ordinal)
                                );
                        if (wordNeeded && a["alignment"]?["normalized_words"] == null)
                        {
                            errors.Add(
                                J.Node(
                                    new Issue("ALIGNMENT_UNAVAILABLE", "Word timing required but unavailable", pointer)
                                )
                            );
                        }
                    }
                }
                else
                {
                    try
                    {
                        var voice =
                            u["audio"]!.Str("voice_alias") == "character"
                                ? Voices.Resolve(
                                    p.Items("characters")
                                        .Single(c => c.Str("character_id") == u.Str("speaker_character_id"))[
                                        "voice_profile"
                                    ]!
                                )
                                : Speech.ResolveVoice(u);
                        if (u["audio"]!["audio_effects"] is { } effects)
                        {
                            voice["audio_effects"] = effects.DeepClone();
                        }
                        voices[u.Str("utterance_id")] = voice;
                        var key = Speech.CacheKey(u.Str("text"), voice);
                        if (!Speech.Cached(key) && keys.Add(key))
                        {
                            chars += J.Normalize(u.Str("text")).EnumerateRunes().Count();
                        }

                        if (u["audio"]!.Str("delivery") != "neutral")
                        {
                            warnings.Add(
                                J.Node(
                                    new Issue(
                                        "DELIVERY_FALLBACK",
                                        "This adapter applies neutral delivery; requested emotion remains visual",
                                        pointer + "/delivery"
                                    )
                                )
                            );
                        }
                    }
                    catch (ToonException ex)
                    {
                        errors.Add(J.Node(new Issue(ex.Code, ex.Message, pointer)));
                    }
                }
            }

            foreach (var e in s.Items("events").Where(e => e["sound"] != null))
            {
                var r = e["sound"]!["asset"]!;
                Asset(r, $"/scenes/{si}/events/sound/asset");
                if (
                    resolvedAssets[r.Str("asset_id") + "@" + r.Str("version")] is JsonNode effect
                    && effect.Str("kind") != "audio"
                )
                {
                    errors.Add(J.Node(new Issue("VALIDATION_ERROR", "Sound asset must be audio", "/events/sound")));
                }
            }

            var oi = 0;
            foreach (var o in s.Items("overlays"))
            {
                if (o["content"]!.Str("kind") == "image")
                {
                    var r = o["content"]!["asset"]!;
                    Asset(r, $"/scenes/{si}/overlays/{oi}/content/asset");
                    if (
                        resolvedAssets[r.Str("asset_id") + "@" + r.Str("version")] is JsonNode a
                        && a.Str("kind") != "image"
                    )
                    {
                        errors.Add(
                            J.Node(
                                new Issue(
                                    "VALIDATION_ERROR",
                                    "Overlay asset must be an image",
                                    $"/scenes/{si}/overlays/{oi}"
                                )
                            )
                        );
                    }
                }

                oi++;
            }

            si++;
        }

        if (p["music"] != null)
        {
            Asset(p["music"]!["asset"]!, "/music/asset");
        }
        double? measuredDuration = null;
        if (errors.Count == 0 && chars == 0)
        {
            try
            {
                var measured = new Dictionary<string, AudioInfo>();
                foreach (var u in p.Items("scenes").SelectMany(s => s.Items("utterances")))
                {
                    if (u["audio"]!.Str("mode") == "recording")
                    {
                        var r = u["audio"]!["asset"]!;
                        var a = resolvedAssets[r.Str("asset_id") + "@" + r.Str("version")]!;
                        measured[u.Str("utterance_id")] = new AudioInfo(
                            "",
                            a.Str("sha256"),
                            a["metadata"]!["samples"]!.GetValue<long>(),
                            a["alignment"],
                            []
                        );
                    }
                    else
                    {
                        var meta = J.Read(
                            Path.Combine(
                                Speech.CacheDir(Speech.CacheKey(u.Str("text"), voices[u.Str("utterance_id")]!)),
                                "result.json"
                            )
                        );
                        var analysis = J.Read(
                            Path.Combine(Config.Data, "audio-cache", "recording-" + meta.Str("sha256"), "analysis.json")
                        );
                        measured[u.Str("utterance_id")] = new AudioInfo(
                            "",
                            meta.Str("sha256"),
                            analysis["samples"]!.GetValue<long>(),
                            meta["alignment"],
                            []
                        );
                    }
                }

                var compiled = Compiler.Compile(p, id, revision, measured);
                foreach (var sound in compiled.Items("sounds"))
                {
                    var r = sound["asset"]!;
                    var asset = resolvedAssets[r.Str("asset_id") + "@" + r.Str("version")]!;
                    J.Require(
                        sound["start_sample"]!.GetValue<long>() + asset["metadata"]!["samples"]!.GetValue<long>()
                            <= sound["scene_end_sample"]!.GetValue<long>(),
                        "VALIDATION_ERROR",
                        "Sound extends past scene; increase tail_ms or move its event"
                    );
                }
                measuredDuration = compiled.Int("total_frames") / 30.0;
            }
            catch (ToonException ex)
            {
                errors.Add(J.Node(new Issue(ex.Code, ex.Message, ex.Pointer)));
            }
        }
        return J.Node(
            new
            {
                schema_valid = true,
                semantic_valid = errors.Count == 0,
                audio_ready = errors.Count == 0 && chars == 0,
                render_ready = errors.Count == 0 && chars == 0,
                measured_duration_seconds = measuredDuration,
                layout_check = "worker preflight and measured browser layout before encode",
                errors,
                warnings,
                missing_dependencies = new JsonArray(
                    errors
                        .Where(e =>
                            new[] { "ASSET_MISSING", "ASSET_VERSION_MISMATCH", "VOICE_NOT_CONFIGURED" }.Contains(
                                e!["code"]!.ToString()
                            )
                        )
                        .Select(e => e!.DeepClone())
                        .ToArray()
                ),
                estimated_new_tts_characters = chars,
                estimated_new_tts_requests = keys.Count,
                estimated_cost = (double?)null,
                currency = (string?)null,
                cost_explanation = "Current account tariff is unknown; code points are an internal limit, not provider billing units",
                resolved_assets = resolvedAssets,
                resolved_rigs = resolvedRigs,
                resolved_environments = resolvedEnvironments,
                resolved_voices = voices,
            }
        );
    }

    public JsonNode Start(
        string id,
        int revision,
        string quality,
        string key,
        bool allow,
        int limit,
        bool studioPreview = false
    )
    {
        J.Require(quality is "preview" or "final", "VALIDATION_ERROR", "quality must be preview or final");
        J.Require(key.Length is > 0 and <= 128, "VALIDATION_ERROR", "idempotency_key must contain 1–128 characters");
        J.Require(
            allow ? limit > 0 && limit <= Config.TtsLimit : limit == 0,
            "TTS_LIMIT_EXCEEDED",
            "Explicit TTS limit is required, within server cap; disabled TTS requires zero"
        );
        var request = J.Node(
            new
            {
                project_id = id,
                revision,
                quality,
                idempotency_key = key,
                allow_new_tts = allow,
                max_new_tts_characters = limit,
            }
        );
        if (studioPreview)
        {
            request["job_kind"] = "studio_preview";
        }

        if (Store.Existing(request) is JsonNode existing)
            return PublicJob(existing);

        var validation = Validate(id, revision);
        if (validation["errors"]!.AsArray().Count > 0)
        {
            var e = validation["errors"]![0]!;
            throw new ToonException(e.Str("code"), e.Str("message"), e.Str("pointer"));
        }

        var characters = validation.Int("estimated_new_tts_characters");
        J.Require(
            characters == 0 || allow,
            "TTS_REQUIRED",
            "Uncached speech requires explicit allow_new_tts and a character limit"
        );
        J.Require(characters <= limit, "TTS_LIMIT_EXCEEDED", $"Need {characters} new characters; allowed {limit}");
        if (characters > 0)
        {
            J.Require(
                !string.IsNullOrWhiteSpace(Secrets.ElevenLabsApiKey()),
                "VOICE_NOT_CONFIGURED",
                "ElevenLabs key is absent"
            );
        }
        var snapshot = J.Node(
            new
            {
                project = Store.Project(id, revision)["project"]!.DeepClone(),
                assets = validation["resolved_assets"]!.DeepClone(),
                rigs = validation["resolved_rigs"]!.DeepClone(),
                environments = validation["resolved_environments"]!.DeepClone(),
                voices = validation["resolved_voices"]!.DeepClone(),
                renderer_version = "1.0.0",
                renderer_fingerprint = Config.RendererFingerprint(),
                style_sha256 = J.FileHash(Path.Combine(Config.Root, "styles/warm_editorial_2d/manifest.json")),
                estimated_new_tts_characters = characters,
            }
        );

        return PublicJob(Store.Enqueue(request, snapshot));
    }

    public JsonNode PublicJob(JsonNode job)
    {
        var copy = job.DeepClone();
        copy.AsObject().Remove("snapshot");
        copy["worker_alive"] = Store.WorkerAlive();

        return copy;
    }

    public JsonNode Job(string id)
    {
        Library.Recover();

        return PublicJob(Store.Job(id));
    }

    public JsonNode Cancel(string id)
    {
        var job = Store.Cancel(id);
        Library.Recover();

        return PublicJob(job);
    }

    public JsonNode Artifacts(string id)
    {
        var job = Store.Job(id);
        if (job.Str("state") != "succeeded")
            return new JsonArray();

        var result = job["artifacts"]!.DeepClone();
        foreach (var item in result.AsArray())
        {
            var a = item!;
            var path = a.Str("local_path");
            J.Require(
                File.Exists(path) && J.FileHash(path) == a.Str("sha256"),
                "VERIFY_FAILED",
                "Published artifact is missing or changed"
            );
        }

        return result;
    }

    public async Task<JsonNode> Doctor(CancellationToken ct)
    {
        Store.Recover();
        var dependencies = new JsonObject();
        foreach (
            var (name, path, args) in new[]
            {
                ("dotnet", "dotnet", new[] { "--version" }),
                ("node", Config.Node, new[] { "--version" }),
                ("ffmpeg", Config.Ffmpeg, new[] { "-version" }),
                ("ffprobe", Config.Ffprobe, new[] { "-version" }),
            }
        )
        {
            try
            {
                dependencies[name] = (await Processes.Run(path, args, Config.Root, ct)).Split('\n')[0].Trim();
            }
            catch (ToonException)
            {
                dependencies[name] = "unavailable";
            }
        }

        return J.Node(
            new
            {
                dependencies,
                worker_alive = Store.WorkerAlive(),
                renderer = File.Exists(Path.Combine(Config.Root, "renderer/render.mjs")),
                font = File.Exists(
                    Path.Combine(
                        Config.Root,
                        "renderer/node_modules/@fontsource/noto-sans/files/noto-sans-cyrillic-700-normal.woff2"
                    )
                ),
                configured_voice_aliases = Config.Voices.AsObject().Select(x => x.Key).ToArray(),
                elevenlabs_key_present = !string.IsNullOrWhiteSpace(Secrets.ElevenLabsApiKey()),
                tts_character_limit = Config.TtsLimit,
                sfx_seconds_limit = Config.SoundEffectLimit,
                data_root = Config.Data,
                worker_assembly_sha256 = J.FileHash(typeof(Worker).Assembly.Location),
                import_roots = Config.ImportRoots,
                worker_configuration = Store.WorkerAlive() && File.Exists(Path.Combine(Config.Data, "worker-info.json"))
                    ? J.Read(Path.Combine(Config.Data, "worker-info.json"))
                    : null,
            }
        );
    }

    public JsonNode Catalog(string kind) =>
        kind switch
        {
            "library" => Library.List(),
            "styles" => J.Node(
                new[]
                {
                    new
                    {
                        style_id = "warm_editorial_2d",
                        version = "1.0.0",
                        guide_resource = "toon://styles/warm_editorial_2d/1.0.0/guide",
                        manifest = J.Read(Path.Combine(Config.Root, "styles/warm_editorial_2d/manifest.json")),
                    },
                }
            ),
            "characters" => Library.CharacterCatalog(),
            "backgrounds" => Library.EnvironmentCatalog(),
            "gestures" => J.Node(Validation.Gestures),
            "sound_effects" => Sounds.Catalog(),
            "props" => Props.Catalog(),
            "voices" => J.Node(
                Config
                    .Voices.AsObject()
                    .Select(x => new
                    {
                        alias = x.Key,
                        provider = "elevenlabs",
                        configured = true,
                    })
            ),
            _ => throw new ToonException("VALIDATION_ERROR", "Unknown catalog kind"),
        };
}
