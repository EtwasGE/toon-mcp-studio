using System.Text.Json.Nodes;
using Json.Schema;

namespace Toon.Core;

public static class Validation
{
    public static readonly string[] Characters = ["buyer", "advisor"];
    public static readonly string[] Backgrounds = ["restaurant_evening", "home_office", "neutral_dark"];
    public static readonly string[] Emotions = ["neutral", "concerned", "annoyed", "surprised", "confident", "pleased"];
    public static readonly string[] Gestures =
    [
        "idle",
        "blink",
        "nod",
        "shake_head",
        "raise_hand",
        "shrug",
        "show_phone",
        "tap_phone",
    ];

    public static void Schema(JsonNode project, string schemaPath)
    {
        var result = JsonSchema
            .FromFile(schemaPath)
            .Evaluate(project, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!result.IsValid)
        {
            var errors = result.Details?.Where(x => !x.IsValid && x.Errors?.Count > 0).ToArray();
            throw new ToonException(
                "VALIDATION_ERROR",
                errors is { Length: > 0 }
                    ? string.Join(
                        "; ",
                        errors.Take(8).Select(x => x.InstanceLocation + ": " + string.Join(", ", x.Errors!.Values))
                    )
                    : "JSON does not satisfy schema",
                errors?.FirstOrDefault()?.InstanceLocation.ToString() ?? ""
            );
        }
    }

    public static void Semantic(JsonNode p, JsonNode? resolvedRigs = null, JsonNode? resolvedEnvironments = null)
    {
        J.Require(
            p["style"]!.Str("style_id") == "warm_editorial_2d" && p["style"]!.Str("version") == "1.0.0",
            "ASSET_VERSION_MISMATCH",
            "Unknown style version",
            "/style"
        );
        J.Require(
            p["captions"]!.Str("preset_id") == "high_contrast_ru",
            "UNSUPPORTED_ACTION",
            "Unknown caption preset",
            "/captions/preset_id"
        );
        var chars = p.Items("characters").Select(x => x.Str("character_id")).ToArray();
        J.Require(chars.Distinct().Count() == chars.Length, "VALIDATION_ERROR", "Duplicate characters", "/characters");
        foreach (var c in p.Items("characters"))
        {
            J.Require(
                resolvedRigs?[c.Str("character_id")] != null
                    || Characters.Contains(c.Str("character_id")) && c.Str("version") == "1.0.0",
                "ASSET_VERSION_MISMATCH",
                "Unknown rig",
                "/characters"
            );
        }
        var ids = new HashSet<string>();

        void Unique(JsonNode n, string field, string path) =>
            J.Require(
                ids.Add(field + ":" + n.Str(field)),
                "VALIDATION_ERROR",
                "Duplicate " + field,
                path + "/" + field
            );

        var sceneIndex = 0;
        foreach (var s in p.Items("scenes"))
        {
            var sp = $"/scenes/{sceneIndex++}";
            Props.ValidateScene(s);
            Unique(s, "scene_id", sp);
            J.Require(
                resolvedEnvironments?[s["background"]!.Str("asset_id") + "@" + s["background"]!.Str("version")] != null
                    || Backgrounds.Contains(s["background"]!.Str("asset_id"))
                        && s["background"]!.Str("version") == "1.0.0",
                "ASSET_MISSING",
                "Unknown background",
                sp + "/background"
            );
            var cast = s.Items("cast").Select(x => x.Str("character_id")).ToArray();
            J.Require(
                cast.Distinct().Count() == cast.Length
                    && s.Items("cast").Select(x => x.Str("placement")).Distinct().Count() == cast.Length,
                "VALIDATION_ERROR",
                "Duplicate cast or placement",
                sp + "/cast"
            );
            foreach (var c in s.Items("cast"))
            {
                J.Require(
                    chars.Contains(c.Str("character_id"))
                        && c.Str("pose_id") == "seated"
                        && Emotions.Contains(c.Str("emotion_id")),
                    "UNSUPPORTED_ACTION",
                    "Unsupported cast/pose/emotion",
                    sp + "/cast"
                );
            }
            void Camera(JsonNode cam, string path)
            {
                if (cam.Str("shot") == "close_up")
                {
                    J.Require(
                        cast.Contains(cam.Str("subject_character_id")),
                        "VALIDATION_ERROR",
                        "Close-up subject is not visible",
                        path
                    );
                }
            }

            Camera(s["camera"]!, sp + "/camera");
            var localUtterances = s.Items("utterances").ToArray();
            J.Require(
                localUtterances.Select(x => x.Str("utterance_id")).Distinct().Count() == localUtterances.Length,
                "VALIDATION_ERROR",
                "Duplicate utterance_id",
                sp + "/utterances"
            );
            var utterances = localUtterances.ToDictionary(x => x.Str("utterance_id"), x => x, StringComparer.Ordinal);
            foreach (var u in s.Items("utterances"))
            {
                Unique(u, "utterance_id", sp + "/utterances");
                J.Require(
                    chars.Contains(u.Str("speaker_character_id")),
                    "VALIDATION_ERROR",
                    "Unknown speaker",
                    sp + "/utterances"
                );
            }

            void Anchor(JsonNode a, string path)
            {
                if (a["utterance_id"] != null)
                {
                    J.Require(
                        utterances.ContainsKey(a.Str("utterance_id")),
                        "VALIDATION_ERROR",
                        "Anchor must refer to a local utterance",
                        path
                    );
                }
            }

            var ei = 0;
            foreach (var e in s.Items("events"))
            {
                var ep = sp + "/events/" + ei++;
                Unique(e, "event_id", ep);
                Anchor(e["at"]!, ep + "/at");
                var a = e["action"]!;
                if (a.Str("kind") == "camera")
                {
                    Camera(a["camera"]!, ep + "/action/camera");
                }
                else if (a.Str("kind") == "prop")
                {
                    Props.ValidateEvent(s, e);
                }
                else
                {
                    J.Require(
                        cast.Contains(a.Str("character_id")),
                        "VALIDATION_ERROR",
                        "Cue character is not visible",
                        ep
                    );
                    J.Require(
                        a.Str("kind") == "gesture"
                            ? Gestures.Contains(a.Str("gesture_id"))
                                || resolvedRigs?[a.Str("character_id")]?["gestures"]?[a.Str("gesture_id")] != null
                            : Emotions.Contains(a.Str("emotion_id")),
                        "UNSUPPORTED_ACTION",
                        "Unsupported gesture or emotion",
                        ep + "/action"
                    );
                }
            }

            var oi = 0;
            foreach (var o in s.Items("overlays"))
            {
                var op = sp + "/overlays/" + oi++;
                Unique(o, "overlay_id", op);
                Anchor(o["start"]!, op + "/start");
                Anchor(o["end"]!, op + "/end");
                if (o["content"]!.Str("kind") == "text")
                {
                    J.Require(
                        new[] { "hook", "cta" }.Contains(o["content"]!.Str("preset_id")),
                        "UNSUPPORTED_ACTION",
                        "Unknown text preset",
                        op + "/content/preset_id"
                    );
                }
            }
        }
    }

    public static void Alignment(JsonNode a, double durationMs)
    {
        J.Require(
            !a.Bool("example_only"),
            "ALIGNMENT_UNAVAILABLE",
            "Illustrative alignment is forbidden in production"
        );
        J.Require(
            Math.Abs(a.Num("duration_ms") - durationMs) <= 50,
            "VALIDATION_ERROR",
            "Alignment duration differs from decoded audio"
        );
        var words = a.Items("normalized_words").ToArray();
        var normalized = J.Words(a.Str("transcript_text"));
        double end = 0;
        J.Require(words.Length == normalized.Length, "VALIDATION_ERROR", "Transcript token count mismatch");
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            J.Require(
                w.Int("index") == i
                    && w.Str("text") == normalized[i]
                    && w.Num("start_ms") >= end
                    && w.Num("end_ms") > w.Num("start_ms")
                    && w.Num("end_ms") <= durationMs,
                "VALIDATION_ERROR",
                "Invalid word interval or transcript",
                $"/normalized_words/{i}"
            );
            end = w.Num("end_ms");
        }

        var next = 0;
        foreach (var span in a.Items("display_spans"))
        {
            J.Require(
                span.Int("start_word_index") == next
                    && span.Int("end_word_index_exclusive") > next
                    && span.Int("end_word_index_exclusive") <= words.Length,
                "VALIDATION_ERROR",
                "Invalid display mapping"
            );
            next = span.Int("end_word_index_exclusive");
        }

        if (a["display_spans"] != null)
        {
            J.Require(next == words.Length, "VALIDATION_ERROR", "Display mapping must cover all words");
        }
    }
}
