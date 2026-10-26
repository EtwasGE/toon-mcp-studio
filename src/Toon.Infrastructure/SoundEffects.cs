using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed class SoundEffects(Studio studio, HttpClient? client = null)
{
    public const string Model = "eleven_text_to_sound_v2";
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(3) };

    public JsonNode Catalog()
    {
        using var db = studio.Store.Open();
        using var query = Store.Command(db, null, "SELECT json FROM assets ORDER BY id");
        using var reader = query.ExecuteReader();
        var items = new JsonArray();
        while (reader.Read())
        {
            var asset = JsonNode.Parse(reader.GetString(0))!;
            if (asset["provenance"]?["source"]?.ToString() == "elevenlabs_sound_effects")
            {
                _ = studio.Assets.Resolve(asset);
                asset["quality"] = PeakDbfs(studio.Assets.Resolve(asset)) < -50 ? "very_quiet" : "ready_for_listening";
                items.Add(asset);
            }
        }
        return items;
    }

    public JsonNode Start(string name, string prompt, double seconds, bool allow, double limit, string key)
    {
        prompt = J.Normalize(prompt);
        J.Require(
            name.Length is > 0 and <= 100
                && prompt.Length is > 0 and <= 1000
                && key.Length is > 0 and <= 128
                && double.IsFinite(seconds)
                && seconds is >= 0.5 and <= 30,
            "VALIDATION_ERROR",
            "Provide a name, prompt, idempotency key and 0.5–30 seconds"
        );
        J.Require(
            double.IsFinite(limit) && limit >= 0 && limit <= studio.Config.SoundEffectLimit,
            "SFX_LIMIT_EXCEEDED",
            "SFX seconds exceed server limit"
        );
        var spec = J.Node(
            new
            {
                text = prompt,
                duration_seconds = seconds,
                model_id = Model,
                loop = false,
            }
        );
        var cacheKey = "sfx-" + J.Hash(J.Canonical(spec));
        var request = J.Node(
            new
            {
                project_id = cacheKey,
                revision = 1,
                quality = "preview",
                job_kind = "sound_effect",
                name,
                spec,
                cache_key = cacheKey,
                allow_new_sfx = allow,
                max_new_sfx_seconds = limit,
                idempotency_key = key,
            }
        );
        if (studio.Store.Existing(request) is JsonNode existing)
            return studio.Job(existing.Str("job_id"));

        J.Require(
            Cached(cacheKey) != null || allow && seconds <= limit,
            "SFX_LIMIT_EXCEEDED",
            "Authorize a sound generation with an explicit duration budget"
        );
        return studio.Job(studio.Store.Enqueue(request, spec).Str("job_id"));
    }

    private JsonNode? Cached(string key)
    {
        var result = Path.Combine(studio.Config.Data, "audio-cache", key, "asset.json");
        if (!File.Exists(result))
            return null;

        var asset = J.Read(result);
        _ = studio.Assets.Resolve(asset);
        J.Require(
            PeakDbfs(studio.Assets.Resolve(asset)) >= -50,
            "SFX_TOO_QUIET",
            "Cached effect is almost silent; choose another asset or explicitly authorize a different prompt"
        );
        return asset;
    }

    private static double PeakDbfs(string wav)
    {
        // Generated/prepared assets are PCM16 mono WAV; inspect the data chunk, not header bytes.
        using var reader = new BinaryReader(File.OpenRead(wav));
        reader.BaseStream.Position = 12;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var tag = new string(reader.ReadChars(4));
            var length = reader.ReadUInt32();
            J.Require(
                length <= reader.BaseStream.Length - reader.BaseStream.Position,
                "VERIFY_FAILED",
                "Invalid prepared WAV chunk"
            );
            if (tag == "data")
            {
                var peak = 0;
                for (long i = 0; i + 1 < length; i += 2)
                {
                    peak = Math.Max(peak, Math.Abs((int)reader.ReadInt16()));
                }
                return peak == 0 ? -120 : 20 * Math.Log10(peak / 32768.0);
            }
            reader.BaseStream.Position += length + (length % 2);
        }
        throw new ToonException("VERIFY_FAILED", "Prepared WAV has no data chunk");
    }

    public async Task Execute(JsonNode job, CancellationToken ct)
    {
        var request = job["request"]!;
        var spec = job["snapshot"]!;
        J.Require(J.Hash(J.Canonical(spec)) == job.Str("snapshot_hash"), "VERIFY_FAILED", "SFX snapshot changed");
        var key = request.Str("cache_key");
        var dir = Path.Combine(studio.Config.Data, "audio-cache", key);
        Directory.CreateDirectory(dir);
        using var singleFlight = new FileStream(
            Path.Combine(dir, "request.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        var asset = Cached(key);
        if (asset == null)
        {
            J.Require(
                request.Bool("allow_new_sfx")
                    && spec.Num("duration_seconds") <= request.Num("max_new_sfx_seconds")
                    && spec.Num("duration_seconds") <= studio.Config.SoundEffectLimit,
                "SFX_LIMIT_EXCEEDED",
                "Sound generation exceeds authorized budget"
            );
            var received = Path.Combine(dir, "received.json");
            var mp3 = Path.Combine(dir, "effect.mp3");
            if (!File.Exists(received))
            {
                // Once dispatched, an uncertain request is never repeated, even under a new job key.
                var previous = studio.Store.Request(key);
                J.Require(
                    previous == null || previous.Str("state") == "rejected",
                    "PROVIDER_OUTCOME_UNKNOWN",
                    "An earlier SFX request exists; inspect provider history before another generation"
                );
                if (previous?["retry_at"] != null)
                {
                    J.Require(
                        DateTimeOffset.Parse(previous.Str("retry_at")) <= DateTimeOffset.UtcNow,
                        "PROVIDER_ERROR",
                        "Provider Retry-After has not elapsed"
                    );
                }
                var secret = Secrets.ElevenLabsApiKey();
                J.Require(!string.IsNullOrWhiteSpace(secret), "VOICE_NOT_CONFIGURED", "ELEVENLABS_API_KEY is absent");
                using var http = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://api.elevenlabs.io/v1/sound-generation?output_format=mp3_44100_128"
                );
                http.Headers.Add("xi-api-key", secret);
                http.Content = JsonContent.Create(spec);
                var journal = J.Node(
                    new
                    {
                        cache_key = key,
                        job_id = job.Str("job_id"),
                        state = "sending",
                        created_at = Store.Now,
                        requested_seconds = spec.Num("duration_seconds"),
                        provider_billing_units = (string?)null,
                    }
                );
                ct.ThrowIfCancellationRequested();
                studio.Store.Journal(key, "sending", journal, previous == null);
                try
                {
                    using var response = await (client ?? SharedClient).SendAsync(
                        http,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct
                    );
                    if (response.Headers.TryGetValues("request-id", out var ids))
                    {
                        journal["request_id"] = ids.FirstOrDefault();
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        journal["http_status"] = (int)response.StatusCode;
                        // Keep only the bounded provider error identifier, never raw response text.
                        try
                        {
                            await using var errorStream = await response.Content.ReadAsStreamAsync(ct);
                            var errorBytes = new byte[4096];
                            var read = await errorStream.ReadAtLeastAsync(errorBytes, errorBytes.Length, false, ct);
                            using var reader = new StreamReader(new MemoryStream(errorBytes, 0, read));
                            var error = JsonNode.Parse(await reader.ReadToEndAsync(ct));
                            var code =
                                error is JsonObject && error["detail"] is JsonObject detail
                                    ? detail["status"]?.ToString()
                                    : null;
                            if (
                                code != null
                                && code.Length <= 80
                                && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                            )
                            {
                                journal["provider_error_code"] = code;
                            }
                        }
                        catch (System.Text.Json.JsonException) { }

                        if ((int)response.StatusCode < 500)
                        {
                            journal["state"] = "rejected";
                            journal["retry_at"] = (
                                response.Headers.RetryAfter?.Date
                                ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero)
                            ).ToString("O");
                            studio.Store.Journal(key, "rejected", journal);
                        }
                        throw new ToonException(
                            (int)response.StatusCode >= 500 ? "PROVIDER_OUTCOME_UNKNOWN" : "PROVIDER_ERROR",
                            $"SFX provider HTTP {(int)response.StatusCode} ({journal["provider_error_code"]?.ToString() ?? "unspecified"}); no automatic retry"
                        );
                    }

                    if (response.Headers.TryGetValues("character-cost", out var cost))
                    {
                        journal["provider_billing_units"] = cost.FirstOrDefault();
                    }
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using (var output = File.Create(mp3 + ".partial"))
                    {
                        var buffer = new byte[81920];
                        int count;
                        long total = 0;
                        while ((count = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            total += count;
                            J.Require(total <= studio.Config.MaxMediaBytes, "PROVIDER_ERROR", "SFX response too large");
                            await output.WriteAsync(buffer.AsMemory(0, count), ct);
                        }
                        J.Require(total > 0, "PROVIDER_ERROR", "Empty SFX response");
                    }
                    File.Move(mp3 + ".partial", mp3, true);
                    journal["sha256"] = J.FileHash(mp3);
                    journal["state"] = "received";
                    J.Write(received, journal);
                    studio.Store.Journal(key, "received", journal);
                    studio.Store.ChangeJob(
                        job.Str("job_id"),
                        j => j["provider_usage_confirmed"]!.AsArray().Add(journal.DeepClone())
                    );
                }
                catch (Exception ex) when (journal.Str("state") == "sending")
                {
                    journal["state"] = "unknown";
                    studio.Store.Journal(key, "unknown", journal);
                    studio.Store.ChangeJob(
                        job.Str("job_id"),
                        j => j["provider_usage_unknown"]!.AsArray().Add(journal.DeepClone())
                    );
                    throw new ToonException(
                        "PROVIDER_OUTCOME_UNKNOWN",
                        ex is OperationCanceledException
                            ? "SFX interrupted after dispatch; inspect provider history"
                            : "SFX response incomplete; no automatic paid retry"
                    );
                }
            }
            J.Require(
                File.Exists(mp3) && J.FileHash(mp3) == J.Read(received).Str("sha256"),
                "VERIFY_FAILED",
                "Received sound changed; refusing paid retry"
            );
            var prepared = await new Media(studio.Config).Prepare(mp3, null, ct);
            var peak = PeakDbfs(prepared.Path);
            J.Require(peak >= -50, "SFX_TOO_QUIET", "Generated effect is almost silent; no automatic paid retry");
            var relative = "assets/" + prepared.Hash + ".wav";
            var destination = Path.Combine(studio.Config.Data, relative);
            if (!File.Exists(destination))
            {
                File.Copy(prepared.Path, destination);
            }
            J.Require(J.FileHash(destination) == prepared.Hash, "VERIFY_FAILED", "Existing SFX asset changed");
            asset = J.Node(
                new
                {
                    asset_id = "sfx_" + key[4..28],
                    version = "1.0.0",
                    kind = "audio",
                    mime = "audio/wav",
                    sha256 = prepared.Hash,
                    bytes = new FileInfo(destination).Length,
                    relative_path = relative,
                    metadata = new
                    {
                        peak_dbfs = peak,
                        samples = prepared.Samples,
                        duration_ms = prepared.DurationMs,
                        sample_rate = 48000,
                        channels = 1,
                    },
                    provenance = new
                    {
                        source = "elevenlabs_sound_effects",
                        name = request.Str("name"),
                        spec,
                        created_at = Store.Now,
                    },
                }
            );
            studio.Store.PutAsset(asset);
            J.Write(Path.Combine(dir, "asset.json"), asset);
        }
        var path = studio.Assets.Resolve(asset);
        var artifact = J.Node(
            new
            {
                artifact_id = "artifact_" + Guid.NewGuid().ToString("N"),
                job_id = job.Str("job_id"),
                project_id = job.Str("project_id"),
                revision = 1,
                name = "sound-effect.wav",
                mime = "audio/wav",
                bytes = new FileInfo(path).Length,
                sha256 = J.FileHash(path),
                duration_seconds = asset["metadata"]!.Num("duration_ms") / 1000,
                resource_uri = new Uri(path).AbsoluteUri,
                local_path = path,
            }
        );
        studio.Store.ChangeJob(
            job.Str("job_id"),
            j =>
            {
                J.Require(
                    j.Str("state") == "running" && !ct.IsCancellationRequested,
                    "CANCELLED",
                    "Cancellation won SFX publication race"
                );
                j["state"] = "succeeded";
                j["stage"] = "verify";
                j["progress"] = 1;
                j["ended_at"] = Store.Now;
                j["sound_asset"] = asset.DeepClone();
                j["artifacts"] = new JsonArray(artifact);
            }
        );
    }
}
