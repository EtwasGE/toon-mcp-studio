using System.Text;
using System.Text.Json.Nodes;

namespace Toon.Core;

public static class CharacterRigs
{
    public static IEnumerable<JsonNode> Nodes(JsonNode rig) => Walk(rig.Items("layers"));

    private static IEnumerable<JsonNode> Walk(IEnumerable<JsonNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;

            foreach (var child in Walk(node.Items("children")))
            {
                yield return child;
            }
        }
    }

    public static void Validate(JsonNode rig, string root, bool recipe = false)
    {
        J.Require(
            Encoding.UTF8.GetByteCount(rig.ToJsonString()) <= 256 * 1024,
            "VALIDATION_ERROR",
            "Rig exceeds 256 KiB"
        );

        Validation.Schema(
            rig,
            Path.Combine(root, "contracts/character-rig.schema.json")
        );

        var nodes = Nodes(rig).ToArray();

        J.Require(
            nodes.Length <= 512,
            "VALIDATION_ERROR",
            "Rig exceeds 512 nodes"
        );

        foreach (var node in nodes)
        {
            var depth = 0;
            for (var parent = node.Parent; parent != null; parent = parent.Parent)
            {
                depth++;
            }

            J.Require(
                depth <= 24,
                "VALIDATION_ERROR",
                "Rig nesting exceeds 8 groups"
            );

            J.Require(
                node["children"] == null || node.Str("type") == "group",
                "VALIDATION_ERROR",
                "Only groups may have children"
            );

            if (node["motion"] != null)
            {
                J.Require(
                    node.Str("type") == "group" && node["pivot"] != null,
                    "VALIDATION_ERROR",
                    "Motion requires a group with a pivot"
                );
            }

            if (node.Str("type") == "cutout")
            {
                J.Require(
                    node["polygon"] != null && node["target"] != null,
                    "VALIDATION_ERROR",
                    "Cutout requires polygon and target"
                );

                J.Require(
                    recipe
                        ? node["source_role"] != null && node["asset"] == null
                        : node["asset"] != null && node["source_role"] == null,
                    "VALIDATION_ERROR",
                    "Recipe must reference imported source roles; prepared rig must reference immutable assets"
                );

                J.Require(
                    node["target"]![2]!.GetValue<double>() > 0 && node["target"]![3]!.GetValue<double>() > 0,
                    "VALIDATION_ERROR",
                    "Cutout target must have positive size"
                );

                if (!recipe)
                {
                    J.Require(
                        node["source_width"] != null && node["source_height"] != null && node["box"] != null,
                        "VALIDATION_ERROR",
                        "Prepared cutout requires measured dimensions and bounding box"
                    );
                }
            }

            if (node.Str("type") == "path")
            {
                J.Require(
                    node["props"]?["d"] != null,
                    "VALIDATION_ERROR",
                    "Path requires geometry"
                );
            }
        }
    }

    public static JsonArray Readiness(JsonNode rig)
    {
        var result = new JsonArray();
        var nodes = Nodes(rig).ToArray();

        foreach (var motion in new[] { "head", "left_arm", "right_arm" })
        {
            Missing(
                nodes.Any(n => n["motion"]?.ToString() == motion),
                "Missing articulated " + motion
            );
        }

        foreach (var emotion in Validation.Emotions)
        {
            Missing(
                rig.Items("emotions").Any(e => e.ToString() == emotion)
                    && nodes.Any(n => n["when"]?.Items("emotion").Any(e => e.ToString() == emotion) == true),
                "Missing expression " + emotion
            );
        }

        for (var mouth = 0; mouth < 4; mouth++)
        {
            Missing(
                nodes.Any(n => n["when"]?.Items("mouth").Any(m => m.GetValue<int>() == mouth) == true),
                "Missing mouth variant " + mouth
            );
        }

        Missing(
            nodes.Any(n => n["when"]?["blink"]?.GetValue<bool>() == true),
            "Missing closed eyes"
        );

        Missing(
            nodes.Any(n => n["when"]?["blink"]?.GetValue<bool>() == false),
            "Missing open eyes"
        );

        Missing(
            nodes.Any(n => n["when"]?["phone"]?.GetValue<bool>() == true),
            "Missing phone grip/prop"
        );

        return result;

        void Missing(bool found, string message)
        {
            if (!found)
            {
                result.Add(J.Node(new Issue("RIG_INCOMPLETE", message, "/rig")));
            }
        }
    }
}
