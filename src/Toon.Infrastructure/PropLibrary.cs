using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed class PropLibrary
{
    private readonly Studio studio;

    public PropLibrary(Studio studio)
    {
        this.studio = studio;
        using var db = studio.Store.Open();
        using var command = Store.Command(
            db,
            null,
            "CREATE TABLE IF NOT EXISTS prop_sounds(prop_id TEXT NOT NULL, action TEXT NOT NULL, revision INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY(prop_id,action))"
        );
        command.ExecuteNonQuery();
    }

    private JsonNode? Sound(string id, string action)
    {
        using var db = studio.Store.Open();
        using var query = Store.Command(
            db,
            null,
            "SELECT json FROM prop_sounds WHERE prop_id=$id AND action=$action",
            ("$id", id),
            ("$action", action)
        );
        return query.ExecuteScalar() is string json ? JsonNode.Parse(json) : null;
    }

    public JsonNode Catalog() =>
        J.Node(
            Props.Definitions.Select(p => new
            {
                kind = "prop",
                prop_id = p.Id,
                version = "1.0.0",
                display_name = p.Name,
                attachments = new[] { "left_hand", "right_hand" },
                views = p.Id == "tablet" ? new[] { "screen", "back" } : null,
                actions = p.Actions.Select(action => new
                {
                    action,
                    contact_frame = Props.ContactFrame,
                    duration_frames = Props.DurationFrames,
                    sound_binding = Sound(p.Id, action),
                }),
            })
        );

    public JsonNode Bind(string id, string action, JsonNode asset, int expected, double gain = -12)
    {
        var definition = Props.Resolve(id, "1.0.0");
        J.Require(definition.Actions.Contains(action), "UNSUPPORTED_ACTION", "Unknown prop action");
        var audio = studio.Store.Asset(asset);
        J.Require(audio.Str("kind") == "audio", "VALIDATION_ERROR", "Prop sound must be audio");
        J.Require(double.IsFinite(gain) && gain is >= -60 and <= 0, "VALIDATION_ERROR", "Sound gain must be -60..0 dB");
        J.Require(audio["metadata"]!.Num("peak_dbfs", 0) >= -50, "SFX_TOO_QUIET", "Sound is too quiet");
        studio.Assets.Resolve(asset);
        J.Require(audio["metadata"]!.Num("duration_ms") <= 2400, "VALIDATION_ERROR", "Prop sound exceeds action tail");
        using var db = studio.Store.Open();
        using var tx = db.BeginTransaction();
        using var query = Store.Command(
            db,
            tx,
            "SELECT revision FROM prop_sounds WHERE prop_id=$id AND action=$action",
            ("$id", id),
            ("$action", action)
        );
        var revision = Convert.ToInt32(query.ExecuteScalar() ?? 0);
        J.Require(revision == expected, "REVISION_CONFLICT", "Prop sound binding changed");
        var result = J.Node(
            new
            {
                prop_id = id,
                action,
                revision = revision + 1,
                asset = asset.DeepClone(),
                gain_db = gain,
            }
        );
        using var save = Store.Command(
            db,
            tx,
            "INSERT INTO prop_sounds VALUES($id,$action,$revision,$json) ON CONFLICT(prop_id,action) DO UPDATE SET revision=$revision,json=$json",
            ("$id", id),
            ("$action", action),
            ("$revision", revision + 1),
            ("$json", result.ToJsonString())
        );
        save.ExecuteNonQuery();
        tx.Commit();
        return result;
    }

    public JsonNode Pin(JsonNode project)
    {
        var copy = project.DeepClone();
        foreach (var scene in copy.Items("scenes"))
        {
            foreach (var cue in scene.Items("events").Where(e => e["action"]!.Str("kind") == "prop"))
            {
                if (cue["sound"] != null)
                {
                    continue;
                }

                var action = cue["action"]!;
                var instance = scene
                    .Items("props")
                    .FirstOrDefault(p => p.Str("instance_id") == action.Str("instance_id"));
                J.Require(instance != null, "VALIDATION_ERROR", "Missing prop instance");
                var binding = Sound(instance!.Str("prop_id"), action.Str("prop_action"));
                J.Require(binding != null, "ASSET_MISSING", "Bind a sound to this prop action or supply event.sound");
                cue["sound"] = J.Node(
                    new
                    {
                        asset = binding!["asset"]!.DeepClone(),
                        gain_db = binding.Num("gain_db", -12),
                        offset_ms = 0,
                    }
                );
            }
        }
        return copy;
    }
}
