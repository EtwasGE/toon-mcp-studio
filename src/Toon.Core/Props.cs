using System.Text.Json.Nodes;

namespace Toon.Core;

public sealed record PropDefinition(string Id, string Name, string[] Actions);

public static class Props
{
    public const int ContactFrame = 18;
    public const int DurationFrames = 90;
    public static readonly PropDefinition[] Definitions =
    [
        new("phone", "Телефон", ["tap", "notification", "error"]),
        new("tablet", "Планшет с надкусанной картошкой", ["tap"]),
        new("cigarette", "Дымящая сигарета", ["puff"]),
        new("beer_mug", "Кружка пива", ["sip", "place"]),
        new("beer_can", "Пивная банка", ["open", "sip"]),
        new("bottle", "Зелёная бутылка", ["open", "sip", "place"]),
    ];

    public static PropDefinition Resolve(string id, string version)
    {
        var definition = Definitions.FirstOrDefault(p => p.Id == id);
        J.Require(definition != null && version == "1.0.0", "ASSET_VERSION_MISMATCH", "Unknown prop/version");
        return definition!;
    }

    public static void ValidateScene(JsonNode scene)
    {
        var instances = scene.Items("props").ToArray();
        J.Require(
            instances.Select(p => p.Str("instance_id")).Distinct().Count() == instances.Length,
            "VALIDATION_ERROR",
            "Duplicate prop instance"
        );
        J.Require(
            instances.Select(p => p.Str("character_id") + ":" + p.Str("hand")).Distinct().Count() == instances.Length,
            "VALIDATION_ERROR",
            "A hand can hold only one prop"
        );
        foreach (var instance in instances)
        {
            Resolve(instance.Str("prop_id"), instance.Str("version"));
            J.Require(
                instance["view"] == null || instance.Str("prop_id") == "tablet",
                "VALIDATION_ERROR",
                "Only a tablet supports screen/back views"
            );
            J.Require(
                scene.Items("cast").Any(c => c.Str("character_id") == instance.Str("character_id")),
                "VALIDATION_ERROR",
                "Prop owner is not in scene cast"
            );
        }
    }

    public static void ValidateEvent(JsonNode scene, JsonNode cue)
    {
        var action = cue["action"]!;
        var instance = scene.Items("props").FirstOrDefault(p => p.Str("instance_id") == action.Str("instance_id"));
        J.Require(instance != null, "VALIDATION_ERROR", "Prop action references missing instance");
        var definition = Resolve(instance!.Str("prop_id"), instance.Str("version"));
        J.Require(instance["view"]?.ToString() != "back", "UNSUPPORTED_ACTION", "Tablet screen is facing away");
        if (action.Str("prop_action") is "tap" or "open")
        {
            J.Require(
                !scene
                    .Items("props")
                    .Any(p =>
                        p.Str("character_id") == instance.Str("character_id") && p.Str("hand") != instance.Str("hand")
                    ),
                "UNSUPPORTED_ACTION",
                "This prop action requires a free second hand"
            );
        }

        J.Require(
            definition.Actions.Contains(action.Str("prop_action")),
            "UNSUPPORTED_ACTION",
            "Unsupported prop action"
        );
        J.Require(cue["sound"] != null, "ASSET_MISSING", "Prop actions require a pinned sound asset");
        J.Require(cue["sound"]!.Int("offset_ms") == 0, "VALIDATION_ERROR", "Prop sound must match its contact frame");
    }
}
