using System.Text.Json.Nodes;

namespace Toon.Core;

public static class Compiler
{
    public static JsonNode Compile(
        JsonNode project,
        string projectId,
        int revision,
        IReadOnlyDictionary<string, AudioInfo> audio
    )
    {
        var scenes = new JsonArray();
        var lines = new JsonArray();
        var captions = new JsonArray();
        long sample = 0;
        foreach (var scene in project.Items("scenes"))
        {
            var start = sample;
            var sceneLines = new Dictionary<string, (JsonNode line, AudioInfo audio)>();
            if (scene.Str("kind") == "hold")
            {
                sample += scene.Int("hold_ms") * 48L;
            }
            else
            {
                sample += scene.Int("lead_in_ms") * 48L;
                foreach (var u in scene.Items("utterances"))
                {
                    var id = u.Str("utterance_id");
                    var a = audio[id];
                    var endSample = sample + a.Samples;
                    if (a.Alignment?["normalized_words"] != null)
                    {
                        Validation.Alignment(a.Alignment, a.DurationMs);
                        var declared = a.Alignment["display_spans"] is null
                            ? J.Normalize(a.Alignment.Str("transcript_text"))
                            : J.Normalize(string.Concat(a.Alignment.Items("display_spans").Select(x => x.Str("text"))));
                        J.Require(
                            declared == J.Normalize(u.Str("text")),
                            "VALIDATION_ERROR",
                            "Recording transcript/display mapping differs from utterance",
                            "/utterances/" + id
                        );
                    }

                    if (a.Alignment?["phrases"] != null)
                    {
                        J.Require(
                            J.Normalize(string.Join(" ", a.Alignment.Items("phrases").Select(x => x.Str("text"))))
                                == J.Normalize(u.Str("text")),
                            "VALIDATION_ERROR",
                            "SRT transcript differs from utterance",
                            "/utterances/" + id
                        );
                    }
                    var line = J.Node(
                        new
                        {
                            utterance_id = id,
                            speaker_character_id = u.Str("speaker_character_id"),
                            text = u.Str("text"),
                            start_sample = sample,
                            end_sample_exclusive = endSample,
                            start_frame = J.Frame(sample / 48.0),
                            end_frame_exclusive = J.Frame(endSample / 48.0),
                            duration_ms = a.DurationMs,
                            audio_hash = a.Hash,
                            audio_file = "media/" + a.Hash + ".wav",
                            envelope = a.Envelope,
                            alignment = a.Alignment?.DeepClone(),
                        }
                    );
                    lines.Add(line);
                    sceneLines[id] = (line, a);
                    if (project["captions"]!.Bool("enabled"))
                    {
                        var mode = project["captions"]!.Str("mode");
                        J.Require(
                            mode != "word" || a.Alignment?["normalized_words"] != null,
                            "ALIGNMENT_UNAVAILABLE",
                            "Word captions require verified word timing",
                            "/captions/mode"
                        );
                        if (mode != "utterance" && a.Alignment?["normalized_words"] != null)
                        {
                            var words = a.Alignment.Items("normalized_words").ToArray();
                            var spans =
                                a.Alignment["display_spans"]?.AsArray().Select(x => x!).ToArray()
                                ?? words
                                    .Select(
                                        (w, i) =>
                                            J.Node(
                                                new
                                                {
                                                    text = w.Str("text"),
                                                    start_word_index = i,
                                                    end_word_index_exclusive = i + 1,
                                                }
                                            )
                                    )
                                    .ToArray();
                            foreach (var span in spans)
                            {
                                var first = words[span.Int("start_word_index")];
                                var last = words[span.Int("end_word_index_exclusive") - 1];
                                Caption(
                                    span.Str("text"),
                                    sample / 48.0 + first.Num("start_ms"),
                                    sample / 48.0 + last.Num("end_ms"),
                                    "word"
                                );
                            }
                        }
                        else if (mode == "auto" && a.Alignment?["phrases"] != null)
                        {
                            foreach (var phrase in a.Alignment.Items("phrases"))
                            {
                                Caption(
                                    phrase.Str("text"),
                                    sample / 48.0 + phrase.Num("start_ms"),
                                    sample / 48.0 + phrase.Num("end_ms"),
                                    "phrase"
                                );
                            }
                        }
                        else
                        {
                            Caption(u.Str("text"), sample / 48.0, endSample / 48.0, "utterance");
                        }
                    }

                    sample = endSample + u.Int("pause_after_ms") * 48L;
                }

                sample += scene.Int("tail_ms") * 48L;
            }

            double Anchor(JsonNode anchor)
            {
                var kind = anchor.Str("kind");
                double ms;
                if (kind == "scene_start")
                {
                    ms = start / 48.0;
                }
                else if (kind == "scene_end")
                {
                    ms = sample / 48.0;
                }
                else
                {
                    var (line, a) = sceneLines[anchor.Str("utterance_id")];
                    ms = line["start_sample"]!.GetValue<long>() / 48.0;
                    if (kind == "utterance_end")
                    {
                        ms += a.DurationMs;
                    }
                    else if (kind.StartsWith("word_", StringComparison.Ordinal))
                    {
                        var words = a.Alignment?.Items("normalized_words").ToArray();
                        var index = anchor.Int("normalized_word_index");
                        J.Require(
                            words != null && index < words.Length,
                            "ALIGNMENT_UNAVAILABLE",
                            "Word anchor requires verified matching alignment",
                            "/events/at"
                        );
                        ms += words![index].Num(kind == "word_start" ? "start_ms" : "end_ms");
                    }
                }

                ms += anchor.Int("offset_ms");
                J.Require(ms >= start / 48.0 && ms <= sample / 48.0, "VALIDATION_ERROR", "Anchor lies outside scene");

                return ms;
            }

            var events = new JsonArray();
            var collisions = new HashSet<string>();
            foreach (var e in scene.Items("events"))
            {
                var f = J.Frame(Anchor(e["at"]!));
                var a = e["action"]!;
                var channel = a.Str("kind") + ":" + a["character_id"]?.ToString();
                J.Require(f < J.Frame(sample / 48.0), "VALIDATION_ERROR", "Event must start before scene ends");
                J.Require(
                    collisions.Add(channel + ":" + f),
                    "AMBIGUOUS_CUE",
                    "Two events occupy the same channel and frame"
                );
                events.Add(
                    J.Node(
                        new
                        {
                            event_id = e.Str("event_id"),
                            frame = f,
                            action = a.DeepClone(),
                        }
                    )
                );
            }

            var cameraEvents = events
                .Select(x => x!)
                .Where(x => x["action"]!.Str("kind") == "camera")
                .OrderBy(x => x.Int("frame"))
                .ToArray();
            for (var i = 0; i < cameraEvents.Length; i++)
            {
                J.Require(
                    cameraEvents[i].Int("frame") + J.Frame(cameraEvents[i]["action"]!.Int("transition_ms"))
                        <= (i + 1 < cameraEvents.Length ? cameraEvents[i + 1].Int("frame") : J.Frame(sample / 48.0)),
                    "AMBIGUOUS_CUE",
                    "Camera transition overlaps next cue or scene end"
                );
            }
            var overlays = new JsonArray();
            foreach (var o in scene.Items("overlays"))
            {
                var from = J.Frame(Anchor(o["start"]!));
                var to = J.Frame(Anchor(o["end"]!));
                J.Require(to > from, "VALIDATION_ERROR", "Overlay interval is empty or reversed");
                overlays.Add(
                    J.Node(
                        new
                        {
                            overlay_id = o.Str("overlay_id"),
                            start_frame = from,
                            end_frame_exclusive = to,
                            content = o["content"]!.DeepClone(),
                        }
                    )
                );
            }

            scenes.Add(
                J.Node(
                    new
                    {
                        scene_id = scene.Str("scene_id"),
                        start_frame = J.Frame(start / 48.0),
                        end_frame_exclusive = J.Frame(sample / 48.0),
                        background = scene["background"]!.DeepClone(),
                        camera = scene["camera"]!.DeepClone(),
                        cast = scene["cast"]!.DeepClone(),
                        events = new JsonArray(
                            events.Select(x => x!).OrderBy(x => x.Int("frame")).Select(x => x.DeepClone()).ToArray()
                        ),
                        overlays,
                    }
                )
            );
        }

        var totalFrames = (int)Math.Ceiling(sample / 1600.0);
        J.Require(
            totalFrames <= 1800,
            "DURATION_LIMIT_EXCEEDED",
            $"Measured timeline is {sample / 48000.0:F3}s; maximum is 60s"
        );
        if (scenes.Count > 0)
        {
            scenes[^1]!["end_frame_exclusive"] = totalFrames;
            var finalDraft = project.Items("scenes").Last();
            var extending = finalDraft
                .Items("overlays")
                .Where(o => o["end"]!.Str("kind") == "scene_end" && o["end"]!.Int("offset_ms") == 0)
                .Select(o => o.Str("overlay_id"))
                .ToHashSet();
            foreach (var overlay in scenes[^1]!.Items("overlays"))
            {
                if (extending.Contains(overlay.Str("overlay_id")))
                {
                    overlay["end_frame_exclusive"] = totalFrames;
                }
            }
        }

        return J.Node(
            new
            {
                schema_version = "1.0.0",
                renderer_version = "1.0.0",
                normalizer_version = "1.0.0",
                project_id = projectId,
                revision,
                fps = 30,
                width = 1080,
                height = 1920,
                total_frames = totalFrames,
                total_samples = sample,
                sample_rate = 48000,
                seed = project.Int("seed"),
                mouth_mode = "approximate/amplitude",
                scenes,
                utterances = lines,
                captions,
                warnings = totalFrames < 600 || totalFrames > 1350
                    ? new[] { "TARGET_DURATION_MISSED" }
                    : Array.Empty<string>(),
            }
        );

        void Caption(string text, double from, double to, string mode)
        {
            var startFrame = J.Frame(from);
            var endFrame = J.Frame(to);
            J.Require(endFrame > startFrame, "VALIDATION_ERROR", "Caption collapses to zero frames");
            captions.Add(
                J.Node(
                    new
                    {
                        text,
                        start_frame = startFrame,
                        end_frame_exclusive = endFrame,
                        mode,
                        rect = new
                        {
                            x = 80,
                            y = 1370,
                            width = 820,
                            height = 190,
                        },
                        font_size = 52,
                    }
                )
            );
        }
    }
}
