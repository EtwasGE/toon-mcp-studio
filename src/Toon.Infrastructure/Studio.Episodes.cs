using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class Studio
{
    public JsonNode EpisodeTemplate() =>
        J.Node(
            new
            {
                instructions = "Choose exact versions from catalogs. Each scene has 1–2 visible characters. Omitted audio uses the approved character binding. Creation never synthesizes speech. Target is a guide; actual duration comes from audio. Edit advanced overlays/events with update_project.",
                schema = J.Read(Path.Combine(Config.Root, "contracts/episode.schema.json")),
                example = J.Read(Path.Combine(Config.Root, "templates/episode.json")),
                characters = Catalog("characters"),
                backgrounds = Catalog("backgrounds"),
            }
        );

    public JsonNode SaveEpisode(JsonNode brief, string? key = null, string? projectId = null, int expected = 0)
    {
        Validation.Schema(brief, Path.Combine(Config.Root, "contracts/episode.schema.json"));
        var scenes = new JsonArray();
        var number = 0;
        foreach (var scene in brief.Items("scenes"))
        {
            number++;
            var cast = scene.Items("characters").Select(c => c.GetValue<string>()).ToArray();
            var utterances = new JsonArray();
            var events = new JsonArray();
            var lineNumber = 0;
            foreach (var line in scene.Items("lines"))
            {
                var id = $"s{number}_u{++lineNumber}";
                var speaker = line.Str("speaker");
                utterances.Add(
                    J.Node(
                        new
                        {
                            utterance_id = id,
                            speaker_character_id = speaker,
                            text = line.Str("text"),
                            audio = line["audio"]?.DeepClone()
                                ?? J.Node(
                                    new
                                    {
                                        mode = "tts",
                                        voice_alias = "character",
                                        delivery = "neutral",
                                    }
                                ),
                            pause_after_ms = line["pause_after_ms"]?.GetValue<int>() ?? 250,
                        }
                    )
                );
                if (line["gesture"] != null)
                {
                    events.Add(
                        J.Node(
                            new
                            {
                                event_id = id + "_gesture",
                                at = new { kind = "utterance_start", utterance_id = id },
                                action = new
                                {
                                    kind = "gesture",
                                    character_id = speaker,
                                    gesture_id = line.Str("gesture"),
                                },
                            }
                        )
                    );
                }

                if (line["sound"] is JsonNode sound)
                {
                    if (line["gesture"] != null)
                    {
                        events[^1]!["sound"] = sound.DeepClone();
                    }
                    else
                    {
                        throw new ToonException("VALIDATION_ERROR", "A line sound requires an explicit gesture");
                    }
                }

                if (line["emotion"] != null)
                {
                    events.Add(
                        J.Node(
                            new
                            {
                                event_id = id + "_emotion",
                                at = new { kind = "utterance_start", utterance_id = id },
                                action = new
                                {
                                    kind = "emotion",
                                    character_id = speaker,
                                    emotion_id = line.Str("emotion"),
                                },
                            }
                        )
                    );
                }

                if (line["close_up"]?.GetValue<bool>() == true)
                {
                    events.Add(
                        J.Node(
                            new
                            {
                                event_id = id + "_camera",
                                at = new { kind = "utterance_start", utterance_id = id },
                                action = new
                                {
                                    kind = "camera",
                                    camera = new { shot = "close_up", subject_character_id = speaker },
                                    transition_ms = 200,
                                },
                            }
                        )
                    );
                }
            }
            scenes.Add(
                J.Node(
                    new
                    {
                        scene_id = "scene_" + number,
                        kind = "dialogue",
                        background = scene["background"]!.DeepClone(),
                        camera = scene["camera"]?.DeepClone() ?? J.Node(new { shot = "wide" }),
                        cast = cast.Select(
                            (id, index) =>
                                new
                                {
                                    character_id = id,
                                    placement = cast.Length == 1 ? "center"
                                    : index == 0 ? "left"
                                    : "right",
                                    pose_id = "seated",
                                    emotion_id = "neutral",
                                }
                        ),
                        utterances,
                        lead_in_ms = scene["lead_in_ms"]?.GetValue<int>() ?? 200,
                        tail_ms = scene["tail_ms"]?.GetValue<int>() ?? 300,
                        events,
                        overlays = new JsonArray(),
                    }
                )
            );
        }
        var project = J.Node(
            new
            {
                schema_version = "1.0.0",
                kind = "draft_project",
                title = brief.Str("title"),
                locale = "ru-RU",
                video = new
                {
                    width = 1080,
                    height = 1920,
                    fps = 30,
                },
                target_duration_seconds = brief["target_duration_seconds"]?.GetValue<int>() ?? 40,
                max_final_duration_seconds = 60,
                seed = brief["seed"]?.GetValue<int>() ?? 42,
                style = new { style_id = "warm_editorial_2d", version = "1.0.0" },
                characters = brief["characters"]!.DeepClone(),
                captions = new
                {
                    enabled = brief["captions"]?.Bool("enabled") ?? true,
                    mode = brief["captions"]?["mode"]?.ToString() ?? "auto",
                    preset_id = "high_contrast_ru",
                },
                scenes,
            }
        );
        if (projectId != null)
            return Update(projectId, expected, project);

        J.Require(
            !string.IsNullOrWhiteSpace(key) && key.Length <= 128,
            "VALIDATION_ERROR",
            "Episode creation requires an idempotency key of 1–128 characters"
        );
        project = Voices.Pin(project);
        CheckDraft(project);
        var saved = Store.SaveProject(project, creationKey: key, creationHash: J.Hash(J.Canonical(brief)));
        saved["readiness"] = Validate(saved.Str("project_id"), saved.Int("revision"));
        return saved;
    }

    public JsonNode ListProjects(int limit = 30, int offset = 0)
    {
        J.Require(
            limit is >= 1 and <= 100 && offset >= 0,
            "VALIDATION_ERROR",
            "limit must be 1–100; offset must be nonnegative"
        );
        using var db = Store.Open();
        using var query = Store.Command(
            db,
            null,
            "SELECT r.id,r.revision,r.json FROM heads h JOIN revisions r ON r.id=h.id AND r.revision=h.revision ORDER BY h.rowid DESC LIMIT $limit OFFSET $offset",
            ("$limit", limit),
            ("$offset", offset)
        );
        using var reader = query.ExecuteReader();
        var items = new JsonArray();
        while (reader.Read())
        {
            var p = JsonNode.Parse(reader.GetString(2))!;
            items.Add(
                J.Node(
                    new
                    {
                        project_id = reader.GetString(0),
                        revision = reader.GetInt32(1),
                        title = p.Str("title"),
                    }
                )
            );
        }
        return J.Node(new { items, next_offset = items.Count == limit ? offset + limit : (int?)null });
    }
}
