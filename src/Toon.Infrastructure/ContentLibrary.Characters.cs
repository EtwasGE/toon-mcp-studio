using System.Text;
using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class ContentLibrary
{
    public JsonArray CharacterCatalog()
    {
        var result = new JsonArray();
        foreach (var id in Validation.Characters)
        {
            result.Add(
                J.Node(
                    new
                    {
                        character_id = id,
                        version = "1.0.0",
                        pose_id = "seated",
                        emotions = Validation.Emotions,
                        gestures = Validation.Gestures,
                    }
                )
            );
        }
        using var db = store.Open();
        using var query = Store.Command(
            db,
            null,
            "SELECT r.id,r.revision FROM library_versions v JOIN library_revisions r ON r.id=v.id AND r.revision=v.revision WHERE json_extract(r.json,'$.submission.kind')='character' AND json_extract(r.json,'$.source')='operator' AND json_extract(r.json,'$.render_supported')=1 ORDER BY r.id,r.revision DESC LIMIT 200"
        );
        using var reader = query.ExecuteReader();
        var revisions = new List<(string Id, int Revision)>();
        while (reader.Read())
        {
            revisions.Add((reader.GetString(0), reader.GetInt32(1)));
        }
        reader.Close();
        foreach (var (id, revision) in revisions)
        {
            var item = Read(db, null, id, revision);
            result.Add(
                J.Node(
                    new
                    {
                        character_id = id,
                        version = item.Str("published_version"),
                        display_name = item["submission"]!.Str("display_name"),
                        pose_id = "seated",
                        emotions = Validation.Emotions,
                        gestures = Validation.Gestures,
                    }
                )
            );
        }
        return result;
    }

    public JsonNode SetCharacterRecipe(string id, int expected, JsonNode recipe)
    {
        J.Require(
            Encoding.UTF8.GetByteCount(recipe.ToJsonString()) <= 300 * 1024,
            "VALIDATION_ERROR",
            "Recipe exceeds 300 KiB"
        );
        Validation.Schema(recipe, Path.Combine(config.Root, "contracts/character-recipe.schema.json"));
        CharacterRigs.Validate(recipe["rig"]!, config.Root, true);
        Recover();
        using (var db = store.Open())
        using (var tx = db.BeginTransaction())
        {
            var item = Editable(db, tx, id, expected, "draft", "review", "published");
            J.Require(
                item["submission"]!.Str("kind") == "character",
                "VALIDATION_ERROR",
                "Recipe belongs to a character"
            );
            item["character_recipe"] = recipe.DeepClone();
            ResetDraft(item);
            Save(db, tx, item, expected);
            tx.Commit();
        }
        return Get(id);
    }

    private JsonNode? PrepareCharacter(JsonNode source, JsonArray errors, JsonArray warnings)
    {
        if (source["character_recipe"] is not JsonNode recipe)
        {
            warnings.Add(
                J.Node(
                    new Issue(
                        "RIG_MAPPING_REQUIRED",
                        "Source package only. Annotate parts, pivots and facial variants using a character recipe",
                        "/character_recipe"
                    )
                )
            );
            return null;
        }
        CharacterRigs.Validate(recipe["rig"]!, config.Root, true);
        foreach (var detail in recipe.Items("missing_details"))
        {
            errors.Add(
                J.Node(new Issue("RIG_DETAIL_REQUIRED", detail.ToString(), "/character_recipe/missing_details"))
            );
        }
        foreach (var detail in recipe.Items("generated_details"))
        {
            warnings.Add(
                J.Node(new Issue("GENERATED_DETAIL_REVIEW", detail.ToString(), "/character_recipe/generated_details"))
            );
        }
        var rig = recipe["rig"]!.DeepClone();
        foreach (var node in CharacterRigs.Nodes(rig).Where(n => n.Str("type") == "cutout"))
        {
            var role = node.Str("source_role");
            var material = source.Items("materials").FirstOrDefault(m => m.Str("role") == role);
            if (material == null)
            {
                errors.Add(
                    J.Node(new Issue("MATERIAL_REQUIRED", "Missing cutout source: " + role, "/materials/" + role))
                );
                return null;
            }
            var asset = store.Asset(material["asset"]!);
            _ = assets.Resolve(material["asset"]!);
            var width = asset["metadata"]!.Int("width");
            var height = asset["metadata"]!.Int("height");
            var points = node.Items("polygon")
                .Select(p => (X: p[0]!.GetValue<double>(), Y: p[1]!.GetValue<double>()))
                .ToArray();
            J.Require(
                points.All(p => p.X >= 0 && p.Y >= 0 && p.X <= width && p.Y <= height),
                "VALIDATION_ERROR",
                "Cutout polygon exceeds measured source dimensions"
            );
            var x = points.Min(p => p.X);
            var y = points.Min(p => p.Y);
            var w = points.Max(p => p.X) - x;
            var h = points.Max(p => p.Y) - y;
            var area =
                Math.Abs(
                    points
                        .Select(
                            (p, i) => p.X * points[(i + 1) % points.Length].Y - points[(i + 1) % points.Length].X * p.Y
                        )
                        .Sum()
                ) / 2;
            J.Require(w > 0 && h > 0 && area > 1, "VALIDATION_ERROR", "Cutout polygon has no area");
            node.AsObject().Remove("source_role");
            node["asset"] = material["asset"]!.DeepClone();
            node["source_width"] = width;
            node["source_height"] = height;
            node["box"] = J.Node(new[] { x, y, w, h });
        }
        CharacterRigs.Validate(rig, config.Root);
        foreach (var issue in CharacterRigs.Readiness(rig))
        {
            errors.Add(issue!.DeepClone());
        }
        warnings.Add(
            J.Node(
                new Issue(
                    "ART_REVIEW_REQUIRED",
                    "Cutouts preserve source pixels. Hidden anatomy and novel views are not reconstructed; review seams, overlaps and all facial states in the character test",
                    "/rig"
                )
            )
        );
        return rig;
    }

    public JsonNode ResolveCharacter(string id, string version)
    {
        if (Validation.Characters.Contains(id))
        {
            J.Require(version == "1.0.0", "ASSET_VERSION_MISMATCH", "Unknown built-in character version");
            var rig = J.Read(Path.Combine(config.Root, "styles/warm_editorial_2d/characters", id + ".rig.json"));
            CharacterRigs.Validate(rig, config.Root);
            return rig;
        }
        var item = Get(id, version: version);
        J.Require(
            item["submission"]!.Str("kind") == "character" && item.Bool("render_supported"),
            "LIBRARY_NOT_READY",
            "Published version is not a renderable character"
        );
        var package = Package(id, item.Int("revision"));
        var result = package["rig"]?.DeepClone();
        J.Require(
            result != null && package.Bool("render_supported"),
            "LIBRARY_NOT_READY",
            "Character has no prepared rig"
        );
        CharacterRigs.Validate(result, config.Root);
        return result;
    }

    public JsonObject ResolveCharacters(JsonNode project)
    {
        var result = new JsonObject();
        foreach (var c in project.Items("characters"))
        {
            result[c.Str("character_id")] = ResolveCharacter(c.Str("character_id"), c.Str("version"));
        }
        return result;
    }

    private void VerifyCharacterTest(
        JsonNode item,
        JsonNode package,
        Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx
    )
    {
        if (!package.Bool("render_supported"))
            return;
        foreach (var node in CharacterRigs.Nodes(package["rig"]!).Where(n => n.Str("type") == "cutout"))
        {
            _ = assets.Resolve(node["asset"]!);
        }
        using var test = Store.Command(
            db,
            tx,
            "SELECT json FROM jobs WHERE state='succeeded' AND json_extract(json,'$.snapshot.character_review.item_id')=$id AND json_extract(json,'$.snapshot.character_review.package_sha256')=$hash",
            ("$id", item.Str("item_id")),
            ("$hash", item["preparation"]!.Str("package_sha256"))
        );
        using var reader = test.ExecuteReader();
        var valid = false;
        while (reader.Read())
        {
            var job = JsonNode.Parse(reader.GetString(0))!;
            var video = job.Items("artifacts").FirstOrDefault(a => a.Str("name") == "video.mp4");
            if (
                video != null
                && File.Exists(video.Str("local_path"))
                && J.FileHash(video.Str("local_path")) == video.Str("sha256")
            )
            {
                valid = true;
                break;
            }
        }
        J.Require(
            valid,
            "CHARACTER_TEST_REQUIRED",
            "Complete and review a verified character test for this exact prepared package before publication"
        );
    }
}
