using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class ContentLibrary
{
    public JsonNode StartPreparation(string id, int expected, string key)
    {
        J.Require(key.Length is > 0 and <= 128, "VALIDATION_ERROR", "idempotency_key must contain 1–128 characters");
        Recover();
        var request = J.Node(
            new
            {
                job_kind = "library_prepare",
                item_id = id,
                expected_revision = expected,
                idempotency_key = key,
            }
        );
        JsonNode job;
        using (var db = store.Open())
        using (var tx = db.BeginTransaction())
        {
            using var find = Store.Command(db, tx, "SELECT request_hash,json FROM jobs WHERE idem=$key", ("$key", key));
            using (var reader = find.ExecuteReader())
                if (reader.Read())
                {
                    J.Require(
                        reader.GetString(0) == J.Hash(J.Canonical(request)),
                        "IDEMPOTENCY_CONFLICT",
                        "Idempotency key belongs to another request"
                    );

                    return PublicJob(JsonNode.Parse(reader.GetString(1))!);
                }
            var item = Editable(db, tx, id, expected, "draft");
            var jobId = "job_" + Guid.NewGuid().ToString("N");
            var snapshot = J.Node(
                new
                {
                    source_revision = item.DeepClone(),
                    preparing_revision = expected + 1,
                    preparation_version = "1.0.0",
                }
            );
            job = J.Node(
                new
                {
                    job_id = jobId,
                    job_kind = "library_prepare",
                    item_id = id,
                    revision = expected,
                    state = "queued",
                    stage = (string?)null,
                    progress = (double?)null,
                    created_at = Store.Now,
                    started_at = (string?)null,
                    ended_at = (string?)null,
                    heartbeat = (string?)null,
                    lease_until = (string?)null,
                    attempt = 0,
                    request,
                    snapshot,
                    snapshot_hash = J.Hash(J.Canonical(snapshot)),
                    artifacts = new JsonArray(),
                    poll_after_ms = 1000,
                }
            );
            using var insert = Store.Command(
                db,
                tx,
                "INSERT INTO jobs VALUES($id,$key,$hash,'queued',NULL,$json)",
                ("$id", jobId),
                ("$key", key),
                ("$hash", J.Hash(J.Canonical(request))),
                ("$json", job.ToJsonString())
            );
            insert.ExecuteNonQuery();
            item["state"] = "preparing";
            item["preparation_job_id"] = jobId;
            Save(db, tx, item, expected);
            tx.Commit();
        }

        return PublicJob(job);
    }

    private JsonNode PublicJob(JsonNode job)
    {
        var result = job.DeepClone();
        result.AsObject().Remove("snapshot");
        result["worker_alive"] = store.WorkerAlive();

        return result;
    }

    public async Task Prepare(JsonNode job, CancellationToken ct)
    {
        var snapshot = job["snapshot"]!;
        J.Require(
            snapshot.Str("preparation_version") == "1.0.0",
            "ASSET_VERSION_MISMATCH",
            "Unsupported library preparation version"
        );
        J.Require(
            J.Hash(J.Canonical(snapshot)) == job.Str("snapshot_hash"),
            "VERIFY_FAILED",
            "Library job snapshot changed"
        );
        var source = snapshot["source_revision"]!;
        CheckSubmission(source["submission"]!);
        var kind = source["submission"]!.Str("kind");
        var errors = new JsonArray();
        var warnings = new JsonArray();
        var prepared = new JsonArray();
        foreach (var role in SubmissionTemplates.RequiredRoles(kind))
        {
            if (!source.Items("materials").Any(m => m.Str("role") == role))
            {
                errors.Add(
                    J.Node(new Issue("MATERIAL_REQUIRED", "Missing material role: " + role, "/materials/" + role))
                );
            }
        }
        if (!source["submission"]!.Bool("rights_confirmed"))
        {
            errors.Add(
                J.Node(
                    new Issue(
                        "RIGHTS_REQUIRED",
                        "Confirm permission to use these materials before publication",
                        "/rights_confirmed"
                    )
                )
            );
        }
        foreach (var material in source.Items("materials"))
        {
            ct.ThrowIfCancellationRequested();
            var path = assets.Resolve(material["asset"]!);
            var asset = store.Asset(material["asset"]!);
            J.Require(
                asset.Str("sha256") == material.Str("sha256"),
                "VERIFY_FAILED",
                "Material hash differs from snapshot"
            );
            try
            {
                await Processes.Run(
                    config.Ffmpeg,
                    ["-v", "error", "-xerror", "-i", path, "-f", "null", "-"],
                    config.Root,
                    ct
                );
            }
            catch (ToonException ex) when (ex.Code == "RENDER_FAILED")
            {
                throw new ToonException(
                    "LIBRARY_PREPARATION_FAILED",
                    "Cannot decode imported material",
                    "/materials/" + material.Str("role")
                );
            }

            prepared.Add(
                J.Node(
                    new
                    {
                        role = material.Str("role"),
                        original_asset = material["asset"]!.DeepClone(),
                        sha256 = material.Str("sha256"),
                        metadata = asset["metadata"]!.DeepClone(),
                        transformation = "none; original bytes preserved",
                    }
                )
            );
            if (kind == "voice" && asset["metadata"]!.Num("duration_ms") < 60000)
            {
                warnings.Add(
                    J.Node(
                        new Issue(
                            "SHORT_VOICE_SAMPLE",
                            "Short sample; suitability for future cloning needs listening",
                            "/materials/sample"
                        )
                    )
                );
            }
        }

        JsonNode? voiceAnalysis = null;
        if (
            kind == "voice"
            && source.Items("materials").FirstOrDefault(m => m.Str("role") == "sample") is JsonNode sample
        )
        {
            voiceAnalysis = await VoiceSamples.Analyze(config, assets, sample["asset"]!, ct);
        }
        var rig = kind == "character" ? PrepareCharacter(source, errors, warnings) : null;
        var environment = kind == "environment" ? PrepareEnvironment(source, errors, warnings) : null;
        var renderSupported = (rig != null || environment != null) && errors.Count == 0;
        if (rig == null && environment == null)
        {
            warnings.Add(
                J.Node(
                    new Issue(
                        "CONTENT_PREPARATION_PENDING",
                        "This package contains source materials, not an animation rig, layered scene or cloned voice",
                        "/materials"
                    )
                )
            );
        }
        var package = J.Node(
            new
            {
                package_version = "1.0.0",
                item_id = source.Str("item_id"),
                source_revision = source.Int("revision"),
                submission = source["submission"]!.DeepClone(),
                materials = prepared,
                errors,
                warnings,
                ready_for_publication = errors.Count == 0,
                render_supported = renderSupported,
                rig,
                environment,
                voice_analysis = voiceAnalysis,
                recipe_sha256 = (source["character_recipe"] ?? source["environment_recipe"]) is JsonNode recipe
                    ? J.Hash(J.Canonical(recipe))
                    : null,
                preparation_method = rig != null || environment != null
                    ? "annotated_cutout_and_vector_layers"
                    : "source_package",
                cloud_requests = false,
            }
        );
        ct.ThrowIfCancellationRequested();
        var packageHash = WriteBlob(package);
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var readJob = Store.Command(db, tx, "SELECT json FROM jobs WHERE id=$id", ("$id", job.Str("job_id")));
        var currentJob = JsonNode.Parse((string)readJob.ExecuteScalar()!)!;
        J.Require(
            currentJob.Str("state") == "running" && !ct.IsCancellationRequested,
            "CANCELLED",
            "Cancellation won library preparation race"
        );
        var item = Editable(db, tx, source.Str("item_id"), snapshot.Int("preparing_revision"), "preparing");
        J.Require(
            item.Str("preparation_job_id") == job.Str("job_id"),
            "REVISION_CONFLICT",
            "Preparation belongs to another job"
        );
        item["state"] = "review";
        item["preparation"] = J.Node(
            new
            {
                package_sha256 = packageHash,
                package_path = BlobPath(packageHash),
                ready_for_publication = errors.Count == 0,
                errors = errors.DeepClone(),
                warnings = warnings.DeepClone(),
                render_supported = renderSupported,
            }
        );
        Save(db, tx, item, item.Int("revision"));
        currentJob["state"] = "succeeded";
        currentJob["stage"] = "verify";
        currentJob["progress"] = 1;
        currentJob["ended_at"] = Store.Now;
        currentJob["result"] = J.Node(
            new
            {
                item_id = item.Str("item_id"),
                revision = item.Int("revision"),
                state = "review",
                ready_for_publication = errors.Count == 0,
                package_sha256 = packageHash,
            }
        );
        using var finish = Store.Command(
            db,
            tx,
            "UPDATE jobs SET state='succeeded',json=$json WHERE id=$id",
            ("$json", currentJob.ToJsonString()),
            ("$id", job.Str("job_id"))
        );
        finish.ExecuteNonQuery();
        tx.Commit();
    }

    public void Recover()
    {
        store.Recover();
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var cmd = Store.Command(
            db,
            tx,
            "SELECT r.json,j.json FROM library_heads h JOIN library_revisions r ON r.id=h.id AND r.revision=h.revision JOIN jobs j ON j.id=json_extract(r.json,'$.preparation_job_id') WHERE json_extract(r.json,'$.state')='preparing' AND j.state IN ('failed','cancelled')"
        );
        using var reader = cmd.ExecuteReader();
        var recovered = new List<(JsonNode, JsonNode)>();
        while (reader.Read())
        {
            recovered.Add((JsonNode.Parse(reader.GetString(0))!, JsonNode.Parse(reader.GetString(1))!));
        }
        reader.Close();
        foreach (var (item, job) in recovered)
        {
            item["state"] = "draft";
            item["last_error"] =
                job["error"]?.DeepClone() ?? J.Node(new { code = "CANCELLED", message = "Preparation was cancelled" });
            Save(db, tx, item, item.Int("revision"));
        }
        tx.Commit();
    }
}
