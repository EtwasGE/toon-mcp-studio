using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class VoiceService
{
    public JsonNode StartPreview(string id, int expected, string text, bool allowNewTts, int maxCharacters, string key)
    {
        text = J.Normalize(text);
        J.Require(
            text.Length is > 0 and <= 240 && key.Length is > 0 and <= 128 && expected > 0 && maxCharacters >= 0,
            "VALIDATION_ERROR",
            "Provide 1–240 characters, a positive revision, nonnegative limit and idempotency key"
        );
        var request = J.Node(
            new
            {
                project_id = "voice_preview_" + id,
                revision = expected,
                quality = "preview",
                job_kind = "voice_preview",
                text,
                allow_new_tts = allowNewTts,
                max_new_tts_characters = maxCharacters,
                idempotency_key = key,
            }
        );
        if (store.Existing(request) is JsonNode existing)
            return studio.Job(existing.Str("job_id"));
        var profile = Profile(id);
        J.Require(profile.Int("revision") == expected, "REVISION_CONFLICT", "Read latest voice profile");
        PreviewReady(profile);
        var voice = Descriptor(profile);
        var cost = studio.Speech.Cached(Speech.CacheKey(text, voice)) ? 0 : text.EnumerateRunes().Count();
        J.Require(
            cost == 0 || allowNewTts && cost <= maxCharacters && cost <= config.TtsLimit,
            "TTS_LIMIT_EXCEEDED",
            "Authorize the new preview synthesis and its character limit"
        );
        return studio.Job(
            store
                .Enqueue(
                    request,
                    J.Node(
                        new
                        {
                            profile,
                            voice,
                            text,
                            estimated_new_tts_characters = cost,
                        }
                    )
                )
                .Str("job_id")
        );
    }

    private void PreviewReady(JsonNode profile)
    {
        Consent(profile);
        J.Require(
            !profile.Bool("requires_verification"),
            "VOICE_VERIFICATION_REQUIRED",
            "Complete provider verification and refresh voice status before synthesis"
        );
        J.Require(
            profile.Str("state") is "pending_review" or "approved" or "rejected",
            "VOICE_NOT_READY",
            "Voice profile is unavailable for a listening review"
        );
    }

    private async Task Preview(JsonNode job, CancellationToken ct)
    {
        var snapshot = job["snapshot"]!;
        var profile = snapshot["profile"]!;
        var current = Profile(profile.Str("profile_id"));
        J.Require(
            current.Int("revision") == profile.Int("revision"),
            "REVISION_CONFLICT",
            "Voice profile changed since preview enqueue"
        );
        PreviewReady(current);
        var text = snapshot.Str("text");
        var voice = snapshot["voice"]!;
        var cost = studio.Speech.Cached(Speech.CacheKey(text, voice)) ? 0 : text.EnumerateRunes().Count();
        var request = job["request"]!;
        J.Require(
            cost == 0
                || request.Bool("allow_new_tts")
                    && cost <= request.Int("max_new_tts_characters")
                    && cost <= config.TtsLimit,
            "TTS_LIMIT_EXCEEDED",
            "Preview synthesis exceeds authorized budget"
        );
        var audio = await studio.Speech.Get(J.Node(new { text }), voice, job.Str("job_id"), ct);
        var directory = Path.Combine(config.Data, "jobs", job.Str("job_id"));
        Directory.CreateDirectory(directory);
        var wav = Path.Combine(directory, "voice-preview.wav");
        File.Copy(audio.Path, wav, true);
        J.Write(
            Path.Combine(directory, "voice-preview.json"),
            J.Node(
                new
                {
                    profile_id = profile.Str("profile_id"),
                    revision = profile.Int("revision"),
                    text,
                    duration_ms = audio.DurationMs,
                    sha256 = J.FileHash(wav),
                    alignment = audio.Alignment,
                    listening_review = "pending_operator",
                    new_tts_characters_estimate = cost,
                }
            )
        );
        var target = Path.Combine(config.Data, "artifacts", job.Str("job_id"));
        var staging = Path.Combine(directory, "publish");
        Directory.CreateDirectory(staging);
        var artifacts = new JsonArray();
        foreach (var name in new[] { "voice-preview.wav", "voice-preview.json" })
        {
            var source = Path.Combine(directory, name);
            File.Copy(source, Path.Combine(staging, name), true);
            var path = Path.Combine(target, name);
            artifacts.Add(
                J.Node(
                    new
                    {
                        artifact_id = "artifact_" + Guid.NewGuid().ToString("N"),
                        job_id = job.Str("job_id"),
                        project_id = job.Str("project_id"),
                        revision = job.Int("revision"),
                        name,
                        mime = name.EndsWith(".wav") ? "audio/wav" : "application/json",
                        bytes = new FileInfo(source).Length,
                        sha256 = J.FileHash(source),
                        duration_seconds = name.EndsWith(".wav") ? audio.DurationMs / 1000 : (double?)null,
                        resource_uri = new Uri(path).AbsoluteUri,
                        local_path = path,
                    }
                )
            );
        }
        store.ChangeJob(
            job.Str("job_id"),
            currentJob =>
            {
                J.Require(
                    currentJob.Str("state") == "running" && !ct.IsCancellationRequested,
                    "CANCELLED",
                    "Cancellation won voice preview publication race"
                );
                Directory.Move(staging, target);
                currentJob["state"] = "succeeded";
                currentJob["stage"] = "verify";
                currentJob["progress"] = 1;
                currentJob["ended_at"] = Store.Now;
                currentJob["artifacts"] = artifacts;
            }
        );
    }

    public void CheckSnapshotVoice(JsonNode voice)
    {
        if (voice["source_profile_id"] == null)
            return;
        var profile = Profile(voice.Str("source_profile_id"));
        J.Require(
            profile.Str("state") == "approved" && profile.Str("voice_id") == voice.Str("voice_id"),
            "VOICE_NOT_READY",
            "Pinned voice is no longer approved"
        );
        Consent(profile);
    }
}
