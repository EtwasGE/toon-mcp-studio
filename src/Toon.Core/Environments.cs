using System.Text;
using System.Text.Json.Nodes;

namespace Toon.Core;

public static class Environments
{
    public static IEnumerable<JsonNode> Nodes(JsonNode environment)
    {
        return environment
            .Items("layers")
            .SelectMany(layer => Walk(layer.Items("nodes")));
    }

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

    public static void Validate(JsonNode environment, string root, bool recipe = false)
    {
        J.Require(
            Encoding.UTF8.GetByteCount(environment.ToJsonString()) <= 256 * 1024,
            "VALIDATION_ERROR",
            "Environment exceeds 256 KiB"
        );

        Validation.Schema(
            environment,
            Path.Combine(root, "contracts/environment.schema.json")
        );

        var nodes = Nodes(environment).ToArray();

        J.Require(
            nodes.Length <= 512,
            "VALIDATION_ERROR",
            "Environment exceeds 512 nodes"
        );

        var layers = environment
            .Items("layers")
            .ToArray();

        J.Require(
            layers.Select(l => l.Str("id")).Distinct().Count() == layers.Length,
            "VALIDATION_ERROR",
            "Duplicate environment layer IDs"
        );

        foreach (var slot in environment["slots"]!.AsObject().Select(p => p.Value!))
        {
            var y = slot.Num("floor_y") - 560 * slot.Num("scale");
            var right = slot.Num("x") + 380 * slot.Num("scale");

            J.Require(
                y >= 0 && right <= 1080,
                "VALIDATION_ERROR",
                "Character slot extends outside canvas"
            );

            var focus = slot["focus"]!;

            J.Require(
                focus[0]!.GetValue<double>() >= slot.Num("x")
                    && focus[0]!.GetValue<double>() <= right
                    && focus[1]!.GetValue<double>() >= y
                    && focus[1]!.GetValue<double>() <= slot.Num("floor_y"),
                "VALIDATION_ERROR",
                "Camera focus must be inside its character slot"
            );

            J.Require(
                layers.All(l => l.Int("depth") != slot.Int("depth")),
                "VALIDATION_ERROR",
                "Layer and character depths must be distinct"
            );
        }

        foreach (var anchor in environment["anchors"]!.AsObject().Select(p => p.Value!))
        {
            J.Require(
                anchor[0]!.GetValue<double>() is >= 0 and <= 1080 && anchor[1]!.GetValue<double>() is >= 0 and <= 1920,
                "VALIDATION_ERROR",
                "Anchor exceeds canvas bounds"
            );
        }

        J.Require(
            layers.Any(l =>
                l.Int("depth") < environment["slots"]!.AsObject().Min(s => s.Value!.Int("depth"))
                && !l.Bool("requires_cast")
            ),
            "VALIDATION_ERROR",
            "Environment needs a background layer behind the characters"
        );

        foreach (var node in nodes)
        {
            var depth = 0;
            for (var parent = node.Parent; parent != null; parent = parent.Parent)
            {
                depth++;
            }

            J.Require(
                depth <= 26, "VALIDATION_ERROR",
                "Environment nesting exceeds 8 groups"
            );

            J.Require(
                node["children"] == null || node.Str("type") is "group" or "defs" or "pattern" or "linearGradient",
                "VALIDATION_ERROR",
                "Unexpected children on environment node"
            );

            if (node.Str("type") == "path")
            {
                J.Require(
                    node["props"]?["d"] != null,
                    "VALIDATION_ERROR",
                    "Path requires geometry"
                );
            }

            if (node.Str("type") == "cutout")
            {
                J.Require(
                    node["polygon"] != null && node["target"] != null,
                    "VALIDATION_ERROR",
                    "Layer cutout needs polygon and target"
                );

                J.Require(
                    recipe
                        ? node["source_role"] != null && node["asset"] == null
                        : node["source_role"] == null && node["asset"] != null,
                    "VALIDATION_ERROR",
                    "Recipes reference source roles; prepared layers reference immutable assets"
                );

                var t = node["target"]!;

                J.Require(
                    t[0]!.GetValue<double>() >= 0
                        && t[1]!.GetValue<double>() >= 0
                        && t[2]!.GetValue<double>() > 0
                        && t[3]!.GetValue<double>() > 0
                        && t[0]!.GetValue<double>() + t[2]!.GetValue<double>() <= 1080
                        && t[1]!.GetValue<double>() + t[3]!.GetValue<double>() <= 1920,
                    "VALIDATION_ERROR",
                    "Layer target must fit the canvas"
                );

                if (!recipe)
                {
                    J.Require(
                        node["source_width"] != null && node["source_height"] != null && node["box"] != null,
                        "VALIDATION_ERROR",
                        "Prepared layer needs measured source dimensions"
                    );
                }
            }
        }

        foreach (var layer in layers)
        {
            var local = Walk(layer.Items("nodes")).ToArray();

            var ids = local
                .Where(n => n["props"]?["id"] != null)
                .Select(n => n["props"]!.Str("id"))
                .ToArray();

            J.Require(
                ids.Distinct().Count() == ids.Length,
                "VALIDATION_ERROR",
                "Duplicate SVG definition IDs in layer"
            );

            foreach (var n in local.Where(n => n["props"]?["fill"]?.ToString().StartsWith("url(#", StringComparison.Ordinal) == true))
            {
                var reference = n["props"]!.Str("fill")[5..^1];

                J.Require(
                    ids.Contains(reference),
                    "VALIDATION_ERROR",
                    "Unknown internal paint reference"
                );
            }
        }
    }
}
