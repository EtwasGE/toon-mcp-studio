using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class Studio
{
    public JsonNode StartCharacterTest(string id, int expected, JsonNode audioAsset, string transcript, string key)
    {
        J.Require(
            expected > 0 && key.Length is > 0 and <= 128,
            "VALIDATION_ERROR",
            "Positive revision and 1–128 character idempotency_key required"
        );
        J.Require(
            transcript.Trim().Length is > 0 and <= 120,
            "VALIDATION_ERROR",
            "Provide the recording transcript (1–120 characters)"
        );
        Validation.Schema(audioAsset, Path.Combine(Config.Root, "contracts/asset-reference.schema.json"));
        var request = J.Node(
            new
            {
                project_id = "character_test_" + id,
                revision = expected,
                quality = "preview",
                job_kind = "character_test",
                item_id = id,
                audio_asset = audioAsset.DeepClone(),
                transcript,
                idempotency_key = key,
                allow_new_tts = false,
                max_new_tts_characters = 0,
            }
        );
        if (Store.Existing(request) is JsonNode existing)
            return PublicJob(existing);
        var item = Library.Get(id);
        J.Require(item.Int("revision") == expected, "REVISION_CONFLICT", "Read the current character revision");
        J.Require(
            item["submission"]!.Str("kind") == "character" && item.Str("state") is "review" or "published",
            "LIBRARY_STATE_CONFLICT",
            "Prepare a character before testing"
        );
        var package = Library.Package(id, expected);
        J.Require(package.Bool("render_supported"), "LIBRARY_NOT_READY", "Resolve missing rig parts before testing");
        var audio = Store.Asset(audioAsset);
        _ = Assets.Resolve(audioAsset);
        J.Require(
            audio.Str("kind") == "audio" && audio["metadata"]!.Num("duration_ms") is >= 4000 and <= 12000,
            "VALIDATION_ERROR",
            "Character test needs a real 4–12 second speech recording and its transcript"
        );
        var rig = package["rig"]!.DeepClone();
        CharacterRigs.Validate(rig, Config.Root);
        var seatedPug = rig["motion"]?["profile"]?.ToString() == "seated_pug";
        var project = CharacterTestProject(id, audioAsset, transcript, seatedPug);
        var rigs = new JsonObject { [id] = rig };
        CheckDraft(project, rigs);
        var resolved = new JsonObject { [audioAsset.Str("asset_id") + "@" + audioAsset.Str("version")] = audio };
        foreach (var node in CharacterRigs.Nodes(rig).Where(n => n.Str("type") == "cutout"))
        {
            var reference = node["asset"]!;
            _ = Assets.Resolve(reference);
            resolved[reference.Str("asset_id") + "@" + reference.Str("version")] = Store.Asset(reference);
        }
        var snapshot = J.Node(
            new
            {
                project,
                assets = resolved,
                rigs,
                environments = Library.ResolveEnvironments(project),
                voices = new JsonObject(),
                renderer_version = "1.0.0",
                renderer_fingerprint = Config.RendererFingerprint(),
                style_sha256 = J.FileHash(Path.Combine(Config.Root, "styles/warm_editorial_2d/manifest.json")),
                estimated_new_tts_characters = 0,
                character_review = new
                {
                    item_id = id,
                    revision = expected,
                    package_sha256 = item["preparation"]!.Str("package_sha256"),
                    test_version = "1.0.0",
                    mouth_mode = "approximate/amplitude",
                    checks = seatedPug
                        ? new[]
                        {
                            "wide",
                            "close_up",
                            "blink",
                            "look_left",
                            "look_right",
                            "open_mouth",
                            "tongue_out",
                            "move_paws",
                            "hide_object",
                            "recorded_speech",
                        }
                        : new[]
                        {
                            "wide",
                            "close_up",
                            "blink",
                            "nod",
                            "shake_head",
                            "raise_hand",
                            "shrug",
                            "show_phone",
                            "six_emotions",
                            "recorded_speech",
                        },
                    visual_review = "pending_operator",
                    listening_review = "pending_operator",
                },
            }
        );
        return PublicJob(Store.Enqueue(request, snapshot));
    }

    private static JsonNode CharacterTestProject(string id, JsonNode audio, string transcript, bool seatedPug = false)
    {
        var scenes = new JsonArray();
        var gestures = seatedPug
            ? new[] { "look_left", "look_right", "open_mouth", "tongue_out", "move_paws", "hide_object" }
            : new[] { "blink", "nod", "shake_head", "raise_hand", "shrug", "show_phone" };
        for (var sceneIndex = 0; sceneIndex < 3; sceneIndex++)
        {
            var events = new JsonArray();
            for (var slot = 0; slot < 2; slot++)
            {
                var index = sceneIndex * 2 + slot;
                var at =
                    slot == 0
                        ? J.Node(new { kind = "scene_start", offset_ms = 0 })
                        : J.Node(
                            new
                            {
                                kind = "utterance_start",
                                utterance_id = "speech_" + sceneIndex,
                                offset_ms = 1000,
                            }
                        );
                events.Add(
                    J.Node(
                        new
                        {
                            event_id = "gesture_" + index,
                            at = at.DeepClone(),
                            action = new
                            {
                                kind = "gesture",
                                character_id = id,
                                gesture_id = gestures[index],
                            },
                        }
                    )
                );
                events.Add(
                    J.Node(
                        new
                        {
                            event_id = "emotion_" + index,
                            at = at.DeepClone(),
                            action = new
                            {
                                kind = "emotion",
                                character_id = id,
                                emotion_id = Validation.Emotions[index],
                            },
                        }
                    )
                );
            }
            scenes.Add(
                J.Node(
                    new
                    {
                        scene_id = "test_" + sceneIndex,
                        kind = "dialogue",
                        background = new { asset_id = "neutral_dark", version = "1.0.0" },
                        camera = sceneIndex == 0 || seatedPug && sceneIndex == 2
                            ? J.Node(new { shot = "wide" })
                            : J.Node(new { shot = "close_up", subject_character_id = id }),
                        cast = new[]
                        {
                            new
                            {
                                character_id = id,
                                placement = "center",
                                pose_id = "seated",
                                emotion_id = "neutral",
                            },
                        },
                        utterances = new[]
                        {
                            new
                            {
                                utterance_id = "speech_" + sceneIndex,
                                speaker_character_id = id,
                                text = transcript,
                                audio = new { mode = "recording", asset = audio.DeepClone() },
                                pause_after_ms = 0,
                            },
                        },
                        lead_in_ms = 1000,
                        tail_ms = 2000,
                        events,
                        overlays = new JsonArray(),
                    }
                )
            );
            if (seatedPug && sceneIndex == 2)
            {
                scenes[sceneIndex]!["props"] = J.Node(
                    new[]
                    {
                        new
                        {
                            instance_id = "pug_test_phone",
                            prop_id = "phone",
                            version = "1.0.0",
                            character_id = id,
                            hand = "left",
                        },
                    }
                );
            }
        }
        return J.Node(
            new
            {
                schema_version = "1.0.0",
                kind = "draft_project",
                title = "Проверка персонажа: эскизы, движения и речь",
                locale = "ru-RU",
                video = new
                {
                    width = 1080,
                    height = 1920,
                    fps = 30,
                },
                target_duration_seconds = 20,
                max_final_duration_seconds = 60,
                seed = 42026,
                style = new { style_id = "warm_editorial_2d", version = "1.0.0" },
                characters = new[] { new { character_id = id, version = "0.0.0" } },
                captions = new
                {
                    enabled = true,
                    mode = "utterance",
                    preset_id = "high_contrast_ru",
                },
                scenes,
            }
        );
    }
}
