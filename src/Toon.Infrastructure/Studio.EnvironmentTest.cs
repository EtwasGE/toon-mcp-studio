using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class Studio
{
    public JsonNode StartEnvironmentTest(string id, int expected, JsonNode audioAsset, string transcript, string key)
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
                project_id = "environment_test_" + id,
                revision = expected,
                quality = "preview",
                job_kind = "environment_test",
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
        J.Require(item.Int("revision") == expected, "REVISION_CONFLICT", "Read the current environment revision");
        J.Require(
            item["submission"]!.Str("kind") == "environment" && item.Str("state") is "review" or "published",
            "LIBRARY_STATE_CONFLICT",
            "Prepare an environment before testing"
        );
        var package = Library.Package(id, expected);
        J.Require(
            package.Bool("render_supported"),
            "LIBRARY_NOT_READY",
            "Resolve environment preparation errors before testing"
        );
        var audio = Store.Asset(audioAsset);
        _ = Assets.Resolve(audioAsset);
        J.Require(
            audio.Str("kind") == "audio" && audio["metadata"]!.Num("duration_ms") is >= 4000 and <= 12000,
            "VALIDATION_ERROR",
            "Environment test needs a real 4–12 second recording and its transcript"
        );
        var environment = package["environment"]!.DeepClone();
        Environments.Validate(environment, Config.Root);
        var project = CharacterTestProject("buyer", audioAsset, transcript);
        project["title"] = "Проверка декорации: два героя, общий и крупные планы";
        project["characters"] = J.Node(
            new[]
            {
                new { character_id = "buyer", version = "1.0.0" },
                new { character_id = "advisor", version = "1.0.0" },
            }
        );
        var index = 0;
        foreach (var scene in project.Items("scenes"))
        {
            var speaker = index == 2 ? "advisor" : "buyer";
            scene["background"] = J.Node(new { asset_id = id, version = "0.0.0" });
            scene["camera"] =
                index == 0
                    ? J.Node(new { shot = "wide" })
                    : J.Node(new { shot = "close_up", subject_character_id = speaker });
            scene["cast"] = J.Node(
                new[]
                {
                    new
                    {
                        character_id = "buyer",
                        placement = "left",
                        pose_id = "seated",
                        emotion_id = "neutral",
                    },
                    new
                    {
                        character_id = "advisor",
                        placement = "right",
                        pose_id = "seated",
                        emotion_id = "pleased",
                    },
                }
            );
            scene["utterances"]![0]!["speaker_character_id"] = speaker;
            scene["events"] = J.Node(
                new[]
                {
                    new
                    {
                        event_id = "environment_nod_" + index,
                        at = new { kind = "scene_start" },
                        action = new
                        {
                            kind = "gesture",
                            character_id = speaker,
                            gesture_id = "nod",
                        },
                    },
                }
            );
            index++;
        }
        var rigs = Library.ResolveCharacters(project);
        var environments = new JsonObject { [id + "@0.0.0"] = environment };
        CheckDraft(project, rigs, environments);
        var resolved = new JsonObject { [audioAsset.Str("asset_id") + "@" + audioAsset.Str("version")] = audio };
        foreach (var node in Environments.Nodes(environment).Where(n => n.Str("type") == "cutout"))
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
                environments,
                voices = new JsonObject(),
                renderer_version = "1.0.0",
                renderer_fingerprint = Config.RendererFingerprint(),
                style_sha256 = J.FileHash(Path.Combine(Config.Root, "styles/warm_editorial_2d/manifest.json")),
                estimated_new_tts_characters = 0,
                environment_review = new
                {
                    item_id = id,
                    revision = expected,
                    package_sha256 = item["preparation"]!.Str("package_sha256"),
                    test_version = "1.0.0",
                    checks = new[]
                    {
                        "wide",
                        "close_up_left",
                        "close_up_right",
                        "two_characters",
                        "foreground_occlusion",
                        "floor_and_scale",
                        "camera_focus",
                    },
                    visual_review = "pending_operator",
                    listening_review = "pending_operator",
                    source_pixels = "preserved",
                    hidden_scenery = "not_reconstructed",
                },
            }
        );
        return PublicJob(Store.Enqueue(request, snapshot));
    }
}
