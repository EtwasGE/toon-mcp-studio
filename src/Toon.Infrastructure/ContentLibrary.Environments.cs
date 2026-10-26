using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class ContentLibrary
{
    public JsonNode SetEnvironmentRecipe(string id, int expected, JsonNode recipe)
    {
        J.Require(
            Encoding.UTF8.GetByteCount(recipe.ToJsonString()) <= 300 * 1024,
            "VALIDATION_ERROR",
            "Recipe exceeds 300 KiB"
        );
        Validation.Schema(recipe, Path.Combine(config.Root, "contracts/environment-recipe.schema.json"));
        Environments.Validate(recipe["environment"]!, config.Root, true);
        Recover();
        using (var db = store.Open())
        using (var tx = db.BeginTransaction())
        {
            var item = Editable(db, tx, id, expected, "draft", "review", "published");
            J.Require(
                item["submission"]!.Str("kind") == "environment",
                "VALIDATION_ERROR",
                "Recipe belongs to an environment"
            );
            item["environment_recipe"] = recipe.DeepClone();
            ResetDraft(item);
            Save(db, tx, item, expected);
            tx.Commit();
        }
        return Get(id);
    }

    private JsonNode? PrepareEnvironment(JsonNode source, JsonArray errors, JsonArray warnings)
    {
        if (source["environment_recipe"] is not JsonNode recipe)
        {
            warnings.Add(
                J.Node(
                    new Issue(
                        "ENVIRONMENT_MAPPING_REQUIRED",
                        "Annotate layers, character slots, anchors and cameras",
                        "/environment_recipe"
                    )
                )
            );
            return null;
        }
        Environments.Validate(recipe["environment"]!, config.Root, true);
        foreach (var detail in recipe.Items("missing_details"))
        {
            errors.Add(
                J.Node(
                    new Issue("ENVIRONMENT_DETAIL_REQUIRED", detail.ToString(), "/environment_recipe/missing_details")
                )
            );
        }
        foreach (var detail in recipe.Items("generated_details"))
        {
            warnings.Add(
                J.Node(new Issue("GENERATED_DETAIL_REVIEW", detail.ToString(), "/environment_recipe/generated_details"))
            );
        }
        var environment = recipe["environment"]!.DeepClone();
        foreach (var node in Environments.Nodes(environment).Where(n => n.Str("type") == "cutout"))
        {
            var role = node.Str("source_role");
            var material = source.Items("materials").FirstOrDefault(m => m.Str("role") == role);
            if (material == null)
            {
                errors.Add(
                    J.Node(new Issue("MATERIAL_REQUIRED", "Missing layer source: " + role, "/materials/" + role))
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
                "Layer polygon exceeds measured source dimensions"
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
            J.Require(w > 0 && h > 0 && area > 1, "VALIDATION_ERROR", "Layer polygon has no area");
            node.AsObject().Remove("source_role");
            node["asset"] = material["asset"]!.DeepClone();
            node["source_width"] = width;
            node["source_height"] = height;
            node["box"] = J.Node(new[] { x, y, w, h });
        }
        Environments.Validate(environment, config.Root);
        warnings.Add(
            J.Node(
                new Issue(
                    "ENVIRONMENT_REVIEW_REQUIRED",
                    "Review wide and both close-ups, masks, foreground occlusion and slot scale. Hidden scenery is not reconstructed",
                    "/environment"
                )
            )
        );
        return environment;
    }

    public JsonNode ResolveEnvironment(string id, string version)
    {
        if (Validation.Backgrounds.Contains(id))
        {
            J.Require(version == "1.0.0", "ASSET_VERSION_MISMATCH", "Unknown built-in environment version");
            var environment = J.Read(
                Path.Combine(config.Root, "styles/warm_editorial_2d/environments", id + ".environment.json")
            );
            Environments.Validate(environment, config.Root);
            return environment;
        }
        var item = Get(id, version: version);
        J.Require(
            item["submission"]!.Str("kind") == "environment" && item.Bool("render_supported"),
            "LIBRARY_NOT_READY",
            "Published version is not a renderable environment"
        );
        var package = Package(id, item.Int("revision"));
        var result = package["environment"]?.DeepClone();
        J.Require(
            result != null && package.Bool("render_supported"),
            "LIBRARY_NOT_READY",
            "Environment has no prepared layers"
        );
        Environments.Validate(result, config.Root);
        return result;
    }

    public JsonObject ResolveEnvironments(JsonNode project)
    {
        var result = new JsonObject();
        foreach (var scene in project.Items("scenes"))
        {
            var reference = scene["background"]!;
            var key = reference.Str("asset_id") + "@" + reference.Str("version");
            if (result[key] == null)
            {
                result[key] = ResolveEnvironment(reference.Str("asset_id"), reference.Str("version"));
            }
        }
        return result;
    }

    public JsonArray EnvironmentCatalog()
    {
        var result = new JsonArray(
            Validation.Backgrounds.Select(id => J.Node(new { asset_id = id, version = "1.0.0" })).ToArray()
        );
        using var db = store.Open();
        using var query = Store.Command(
            db,
            null,
            "SELECT r.id,r.revision FROM library_versions v JOIN library_revisions r ON r.id=v.id AND r.revision=v.revision WHERE json_extract(r.json,'$.submission.kind')='environment' AND json_extract(r.json,'$.source')='operator' AND json_extract(r.json,'$.render_supported')=1 ORDER BY r.id,r.revision DESC LIMIT 200"
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
                        asset_id = id,
                        version = item.Str("published_version"),
                        display_name = item["submission"]!.Str("display_name"),
                    }
                )
            );
        }
        return result;
    }

    private void VerifyEnvironmentTest(JsonNode item, JsonNode package, SqliteConnection db, SqliteTransaction tx)
    {
        if (item["submission"]!.Str("kind") != "environment" || !package.Bool("render_supported"))
            return;
        foreach (var node in Environments.Nodes(package["environment"]!).Where(n => n.Str("type") == "cutout"))
        {
            _ = assets.Resolve(node["asset"]!);
        }
        using var query = Store.Command(
            db,
            tx,
            "SELECT json FROM jobs WHERE state='succeeded' AND json_extract(json,'$.snapshot.environment_review.item_id')=$id AND json_extract(json,'$.snapshot.environment_review.package_sha256')=$hash",
            ("$id", item.Str("item_id")),
            ("$hash", item["preparation"]!.Str("package_sha256"))
        );
        using var reader = query.ExecuteReader();
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
            "ENVIRONMENT_TEST_REQUIRED",
            "Complete and review an environment test for this exact package before publication"
        );
    }
}
