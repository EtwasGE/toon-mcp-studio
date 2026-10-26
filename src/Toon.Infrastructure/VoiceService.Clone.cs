using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class VoiceService
{
    private static int CloneLimit =>
        int.TryParse(Environment.GetEnvironmentVariable("TOON_MAX_VOICE_CLONES"), out var value)
            ? Math.Clamp(value, 0, 100)
            : 0;

    private static void CloneMode()
    {
        J.Require(
            Environment.GetEnvironmentVariable("TOON_ALLOW_VOICE_CLONING") == "1" && CloneLimit > 0,
            "VOICE_CLONING_DISABLED",
            "Set TOON_ALLOW_VOICE_CLONING=1 and an explicit TOON_MAX_VOICE_CLONES installation-wide request budget"
        );
    }

    public JsonNode StartClone(
        string itemId,
        int expected,
        bool allowCloudUpload,
        int maxCloneRequests,
        bool sampleReviewed,
        string reviewNote,
        string key
    )
    {
        J.Require(
            expected > 0 && key.Length is > 0 and <= 128 && reviewNote.Trim().Length is > 0 and <= 1000,
            "VALIDATION_ERROR",
            "Provide revision, idempotency key and sample listening review note"
        );
        var request = J.Node(
            new
            {
                project_id = "voice_clone_" + itemId,
                revision = expected,
                quality = "preview",
                job_kind = "voice_clone",
                item_id = itemId,
                allow_cloud_upload = allowCloudUpload,
                max_clone_requests = maxCloneRequests,
                sample_reviewed = sampleReviewed,
                review_note = reviewNote,
                idempotency_key = key,
            }
        );
        if (store.Existing(request) is JsonNode existing)
            return studio.Job(existing.Str("job_id"));
        J.Require(
            allowCloudUpload && maxCloneRequests == 1,
            "VOICE_CLONING_DISABLED",
            "Authorize one cloud clone request explicitly"
        );
        J.Require(
            sampleReviewed,
            "REVIEW_REQUIRED",
            "Listen to the original: one consenting speaker, clean speech, no music; confirm the review"
        );
        CloneMode();
        var item = studio.Library.Get(itemId);
        J.Require(item.Int("revision") == expected, "REVISION_CONFLICT", "Read current voice source revision");
        RequireConsent(item);
        J.Require(item.Str("state") is "review" or "published", "LIBRARY_NOT_READY", "Prepare the voice sample first");
        var package = studio.Library.Package(itemId, expected);
        J.Require(
            package["voice_analysis"]?.Bool("technical_ready") == true && package.Bool("ready_for_publication"),
            "VOICE_SAMPLE_NOT_READY",
            "Resolve voice sample technical checks first"
        );
        var material = item.Items("materials").Single(m => m.Str("role") == "sample");
        _ = studio.Assets.Resolve(material["asset"]!);
        var operation =
            "clone-"
            + J.Hash(
                J.Canonical(
                    J.Node(
                        new
                        {
                            provider = "elevenlabs",
                            item_id = itemId,
                            sample_sha256 = material.Str("sha256"),
                        }
                    )
                )
            );
        var snapshot = J.Node(
            new
            {
                source = item,
                sample = material,
                analysis = package["voice_analysis"]!.DeepClone(),
                operation_key = operation,
                consent_revision = expected,
                adapter_version = "ivc-v1",
            }
        );
        return studio.Job(store.Enqueue(request, snapshot).Str("job_id"));
    }

    private JsonNode Reserve(JsonNode job)
    {
        CloneMode();
        var key = job["snapshot"]!.Str("operation_key");
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var previousQuery = Store.Command(
            db,
            tx,
            "SELECT json FROM provider_requests WHERE cache_key=$key",
            ("$key", key)
        );
        var previous = previousQuery.ExecuteScalar() is string json ? JsonNode.Parse(json) : null;
        J.Require(
            previous == null || previous.Str("state") == "rejected",
            "PROVIDER_OUTCOME_UNKNOWN",
            "Clone may already exist; reconcile its provider ID instead of sending another clone request"
        );
        if (previous?["retry_at"] != null)
        {
            J.Require(
                DateTimeOffset.Parse(previous.Str("retry_at")) <= DateTimeOffset.UtcNow,
                "PROVIDER_ERROR",
                "Provider Retry-After has not elapsed"
            );
        }
        using var usedQuery = Store.Command(
            db,
            tx,
            "SELECT COALESCE(SUM(COALESCE(json_extract(json,'$.attempts'),1)),0) FROM provider_requests WHERE cache_key LIKE 'clone-%'"
        );
        J.Require(
            Convert.ToInt32(usedQuery.ExecuteScalar()) < CloneLimit,
            "VOICE_CLONE_LIMIT",
            "Installation-wide clone request budget is exhausted; rejected and uncertain requests count too"
        );
        var journal = J.Node(
            new
            {
                cache_key = key,
                state = "sending",
                job_id = job.Str("job_id"),
                attempts = (previous?.Int("attempts") ?? 0) + 1,
                created_at = Store.Now,
                provider = "elevenlabs",
                request_id = (string?)null,
            }
        );
        using var write = Store.Command(
            db,
            tx,
            "INSERT INTO provider_requests VALUES($key,'sending',$json) ON CONFLICT(cache_key) DO UPDATE SET state='sending',json=$json",
            ("$key", key),
            ("$json", journal.ToJsonString())
        );
        write.ExecuteNonQuery();
        tx.Commit();
        return journal;
    }

    public async Task Execute(JsonNode job, CancellationToken ct)
    {
        J.Require(
            J.Hash(J.Canonical(job["snapshot"]!)) == job.Str("snapshot_hash"),
            "VERIFY_FAILED",
            "Voice job snapshot changed"
        );
        if (job.Str("job_kind") == "voice_preview")
        {
            await Preview(job, ct);
            return;
        }
        var snapshot = job["snapshot"]!;
        var source = snapshot["source"]!;
        var current = studio.Library.Get(source.Str("item_id"));
        RequireConsent(current);
        J.Require(
            current.Int("revision") == source.Int("revision"),
            "REVISION_CONFLICT",
            "Voice source changed after clone enqueue; review and submit a new request"
        );
        var samplePath = studio.Assets.Resolve(snapshot["sample"]!["asset"]!);
        var operation = snapshot.Str("operation_key");
        var previous = store.Request(operation);
        if (previous?.Str("state") == "created")
        {
            Complete(job, new { profile = Profile(previous.Str("profile_id")), reused = true }, ct);
            return;
        }
        J.Require(
            job["request"]!.Bool("allow_cloud_upload")
                && job["request"]!.Int("max_clone_requests") == 1
                && job["request"]!.Bool("sample_reviewed"),
            "VOICE_CLONING_DISABLED",
            "Clone authorization missing from request"
        );
        var secret = Secret();
        CloneMode();
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/voices/add");
        request.Headers.Add("xi-api-key", secret);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(source["submission"]!.Str("display_name")), "name");
        form.Add(new StringContent(source["submission"]!.Str("description")), "description");
        form.Add(new StringContent("false"), "remove_background_noise");
        form.Add(new StringContent(J.Node(new { toon_request = operation }).ToJsonString()), "labels");
        var file = new StreamContent(File.OpenRead(samplePath));
        file.Headers.ContentType = new MediaTypeHeaderValue(
            Path.GetExtension(samplePath) == ".wav" ? "audio/wav" : "audio/mpeg"
        );
        form.Add(file, "files", "sample" + Path.GetExtension(samplePath));
        request.Content = form;
        var journal = Reserve(job);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            if (response.Headers.TryGetValues("request-id", out var ids))
            {
                journal["request_id"] = ids.FirstOrDefault();
            }

            if (!response.IsSuccessStatusCode)
            {
                var rejected = (int)response.StatusCode is >= 400 and < 500;
                journal["state"] = rejected ? "rejected" : "unknown";
                journal["http_status"] = (int)response.StatusCode;
                if (response.Headers.RetryAfter != null)
                {
                    journal["retry_at"] = (
                        response.Headers.RetryAfter.Date
                        ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter.Delta ?? TimeSpan.FromSeconds(60))
                    ).ToString("O");
                }
                store.Journal(operation, journal.Str("state"), journal);
                throw new ToonException(
                    rejected ? "PROVIDER_ERROR" : "PROVIDER_OUTCOME_UNKNOWN",
                    rejected
                        ? "Provider rejected voice cloning; response body is not logged"
                        : "Clone outcome unknown; reconcile provider history before another request"
                );
            }
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            var profile = RecordClone(
                job,
                body.Str("voice_id"),
                body["requires_verification"]?.GetValue<bool>() ?? true
            );
            journal["state"] = "created";
            journal["profile_id"] = profile.Str("profile_id");
            store.Journal(operation, "created", journal);
            Complete(job, new { profile, reused = false }, ct);
        }
        catch (Exception ex) when (ex is not ToonException || journal.Str("state") == "sending")
        {
            if (journal.Str("state") == "sending")
            {
                journal["state"] = "unknown";
                store.Journal(operation, "unknown", journal);
            }

            if (journal.Str("state") == "created" && ex is OperationCanceledException)
                throw;
            throw new ToonException(
                "PROVIDER_OUTCOME_UNKNOWN",
                "Clone request interrupted or response invalid; do not automatically repeat it. Reconcile the provider voice ID"
            );
        }
    }

    private JsonNode RecordClone(JsonNode job, string voiceId, bool requiresVerification)
    {
        VoiceId(voiceId);
        var snapshot = job["snapshot"]!;
        var id = "voice_" + J.Hash(snapshot.Str("operation_key"))[..32];
        using (var db = store.Open())
        using (var exists = Store.Command(db, null, "SELECT 1 FROM voice_profiles WHERE id=$id", ("$id", id)))
        {
            if (exists.ExecuteScalar() != null)
            {
                var profile = Profile(id);
                J.Require(
                    profile.Str("voice_id") == voiceId,
                    "VOICE_RECONCILIATION_MISMATCH",
                    "A different provider voice was already recorded for this operation"
                );
                return profile;
            }
        }
        return SaveProfile(
            J.Node(
                new
                {
                    profile_id = id,
                    revision = 0,
                    provider = "elevenlabs",
                    voice_id = voiceId,
                    display_name = snapshot["source"]!["submission"]!.Str("display_name"),
                    source_item_id = snapshot["source"]!.Str("item_id"),
                    source_revision = snapshot["source"]!.Int("revision"),
                    source_sample_sha256 = snapshot["sample"]!.Str("sha256"),
                    operation_key = snapshot.Str("operation_key"),
                    requires_verification = requiresVerification,
                    state = requiresVerification ? "verification_required" : "pending_review",
                    created_at = Store.Now,
                    review_note = (string?)null,
                }
            ),
            0
        );
    }

    public async Task<JsonNode> Reconcile(string jobId, string providerVoiceId, string note, CancellationToken ct)
    {
        J.Require(note.Trim().Length is > 0 and <= 1000, "VALIDATION_ERROR", "Provide reconciliation note");
        VoiceId(providerVoiceId);
        var job = store.Job(jobId);
        J.Require(
            job["job_kind"]?.ToString() == "voice_clone" && job.Str("state") is "failed" or "cancelled",
            "VOICE_NOT_READY",
            "Reconcile a terminal clone job only"
        );
        J.Require(
            J.Hash(J.Canonical(job["snapshot"]!)) == job.Str("snapshot_hash"),
            "VERIFY_FAILED",
            "Voice job snapshot changed"
        );
        RequireConsent(studio.Library.Get(job["snapshot"]!["source"]!.Str("item_id")));
        var journal = store.Request(job["snapshot"]!.Str("operation_key"));
        J.Require(journal != null, "VOICE_NOT_READY", "No clone request was dispatched");
        var body = await GetProviderVoice(providerVoiceId, ct);
        J.Require(
            body["labels"]?["toon_request"]?.ToString() == job["snapshot"]!.Str("operation_key"),
            "VOICE_RECONCILIATION_MISMATCH",
            "Provider voice has no matching Toon request label"
        );
        var profile = RecordClone(job, providerVoiceId, RequiresVerification(body));
        journal["state"] = "created";
        journal["profile_id"] = profile.Str("profile_id");
        journal["reconciliation_note"] = note.Trim();
        store.Journal(job["snapshot"]!.Str("operation_key"), "created", journal);
        return profile;
    }

    public async Task<JsonNode> Refresh(string id, int expected, CancellationToken ct)
    {
        var profile = Profile(id);
        J.Require(profile.Int("revision") == expected, "REVISION_CONFLICT", "Read latest voice profile");
        Consent(profile);
        var body = await GetProviderVoice(profile.Str("voice_id"), ct);
        var required = RequiresVerification(body);
        if (required == profile.Bool("requires_verification"))
            return profile;
        profile["requires_verification"] = required;
        profile["state"] = required ? "verification_required" : "pending_review";
        profile["review_note"] = null;
        return SaveProfile(profile, expected);
    }

    private static bool RequiresVerification(JsonNode body) =>
        body["voice_verification"] is not JsonNode verification
        || !(
            verification["is_verified"]?.GetValue<bool>() == true
            || verification["requires_verification"]?.GetValue<bool>() == false
        );

    private async Task<JsonNode> GetProviderVoice(string id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices/" + id);
        request.Headers.Add("xi-api-key", Secret());
        using var response = await client.SendAsync(request, ct);
        J.Require(
            response.IsSuccessStatusCode,
            "PROVIDER_ERROR",
            "Provider voice lookup failed; no clone request was made"
        );
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        J.Require(body.Str("voice_id") == id, "PROVIDER_ERROR", "Provider returned a different voice");
        return body;
    }

    private void Complete(JsonNode job, object result, CancellationToken ct) =>
        store.ChangeJob(
            job.Str("job_id"),
            j =>
            {
                J.Require(
                    j.Str("state") == "running" && !ct.IsCancellationRequested,
                    "CANCELLED",
                    "Voice operation was cancelled; a dispatched cloud request may still have completed"
                );
                j["state"] = "succeeded";
                j["stage"] = "verify";
                j["progress"] = 1;
                j["ended_at"] = Store.Now;
                j["result"] = J.Node(result);
            }
        );
}
