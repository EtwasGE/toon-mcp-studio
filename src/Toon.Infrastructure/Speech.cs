using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed class Speech(Config config, Store store, HttpClient? client = null)
{
    public const string DefaultModel = "eleven_v4";
    public const string DefaultLanguage = "ru";

    public static JsonNode V4Settings(JsonNode? settings) =>
        J.Node(
            new
            {
                stability = settings?["stability"]?.GetValue<double>() ?? .5,
                similarity_boost = settings?["similarity_boost"]?.GetValue<double>() ?? .75,
            }
        );

    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

    public JsonNode ResolveVoice(JsonNode utterance)
    {
        var alias = utterance["audio"]!.Str("voice_alias");
        var voice = config.Voices[alias]?.DeepClone();
        J.Require(voice != null, "VOICE_NOT_CONFIGURED", "Voice alias is not configured: " + alias);
        J.Require(
            voice!["voice_id"] != null
                && Regex.IsMatch(voice.Str("voice_id"), "^[A-Za-z0-9_-]{1,128}$")
                && voice["model_id"] != null
                && voice.Str("provider") == "elevenlabs",
            "VOICE_NOT_CONFIGURED",
            "Invalid ElevenLabs voice binding"
        );

        return J.Node(
            new
            {
                provider = "elevenlabs",
                voice_id = voice.Str("voice_id"),
                model_id = DefaultModel,
                language_code = DefaultLanguage,
                voice_settings = V4Settings(voice["voice_settings"]),
                pronunciation_dictionary_locators = voice["pronunciation_dictionary_locators"]?.DeepClone()
                    ?? new JsonArray(),
                dictionary_version = voice["dictionary_version"]?.ToString() ?? "none",
                delivery_applied = "neutral",
                output_format = "mp3_44100_128",
                adapter_version = "v4-dialogue-1",
                normalizer_version = "1.0.0",
            }
        );
    }

    public static string CacheKey(string text, JsonNode voice) =>
        J.Hash(J.Canonical(J.Node(new { text = J.Normalize(text), voice })));

    public string CacheDir(string key) => Path.Combine(config.Data, "audio-cache", "tts-" + key);

    public bool Cached(string key)
    {
        var dir = CacheDir(key);
        var meta = Path.Combine(dir, "result.json");
        if (!File.Exists(meta))
            return false;

        try
        {
            var m = J.Read(meta);

            return File.Exists(Path.Combine(dir, "speech.mp3"))
                && J.FileHash(Path.Combine(dir, "speech.mp3")) == m.Str("sha256");
        }
        catch
        {
            return false;
        }
    }

    public async Task<AudioInfo> Get(JsonNode utterance, JsonNode voice, string jobId, CancellationToken ct)
    {
        var text = J.Normalize(utterance.Str("text"));
        var key = CacheKey(text, voice);
        var dir = CacheDir(key);
        Directory.CreateDirectory(dir);
        // OS lock supplements the durable request journal; only one process can send this key.
        await using var singleFlight = new FileStream(
            Path.Combine(dir, "single-flight.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        if (Cached(key))
            return await new Media(config).Prepare(
                Path.Combine(dir, "speech.mp3"),
                J.Read(Path.Combine(dir, "result.json"))["alignment"]?.DeepClone(),
                ct
            );

        var previous = store.Request(key);
        if (previous?.Str("state") == "received" && File.Exists(Path.Combine(dir, "provider-result.json")))
            return await CompleteReceived(previous);

        J.Require(
            previous == null || previous.Str("state") == "rejected",
            "PROVIDER_OUTCOME_UNKNOWN",
            "A previous request may have been charged; reconcile provider history before requesting another synthesis"
        );
        if (previous?["retry_at"] != null)
        {
            J.Require(
                DateTimeOffset.Parse(previous.Str("retry_at")) <= DateTimeOffset.UtcNow,
                "PROVIDER_ERROR",
                "Retry-After has not elapsed"
            );
        }
        var secret = Secrets.ElevenLabsApiKey();
        J.Require(!string.IsNullOrWhiteSpace(secret), "VOICE_NOT_CONFIGURED", "ELEVENLABS_API_KEY is not configured");
        var count = text.EnumerateRunes().Count();
        var journal = J.Node(
            new
            {
                cache_key = key,
                job_id = jobId,
                state = "sending",
                created_at = Store.Now,
                requested_characters = count,
                provider_billing_units = (int?)null,
                request_id = (string?)null,
            }
        );
        store.Journal(key, "sending", journal, previous == null);
        var useV4 = voice.Str("model_id") == DefaultModel;
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            useV4
                ? "https://api.elevenlabs.io/v1/text-to-dialogue/with-timestamps?output_format=mp3_44100_128"
                : "https://api.elevenlabs.io/v1/text-to-speech/"
                    + voice.Str("voice_id")
                    + "/with-timestamps?output_format=mp3_44100_128"
        );
        request.Headers.Add("xi-api-key", secret);
        var payload = J.Node(
            new
            {
                model_id = voice.Str("model_id"),
                pronunciation_dictionary_locators = voice["pronunciation_dictionary_locators"]!.DeepClone(),
            }
        );
        if (useV4)
        {
            payload["language_code"] = voice["language_code"]?.ToString() ?? DefaultLanguage;
            var settings = V4Settings(voice["voice_settings"]);
            payload["inputs"] = J.Node(new[] { new { text, voice_id = voice.Str("voice_id") } });
            payload["settings"] = J.Node(
                new
                {
                    stability = settings["stability"]!.GetValue<double>(),
                    similarity = settings["similarity_boost"]!.GetValue<double>(),
                }
            );
        }
        else
        {
            payload["text"] = text;
            payload["voice_settings"] = voice["voice_settings"]!.DeepClone();
        }

        if (voice["audio_effects"] is { } effects)
        {
            payload["audio_effects"] = effects.DeepClone();
        }
        request.Content = JsonContent.Create(payload);
        HttpResponseMessage response;
        try
        {
            response = await (client ?? SharedClient).SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Unknown();
            throw new ToonException(
                "PROVIDER_OUTCOME_UNKNOWN",
                "Connection ended after request dispatch; no automatic paid retry. Reconcile provider history."
            );
        }

        using (response)
        {
            if (response.Headers.TryGetValues("request-id", out var ids))
            {
                journal["request_id"] = ids.FirstOrDefault();
            }

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500)
                {
                    Unknown();
                    throw new ToonException(
                        "PROVIDER_OUTCOME_UNKNOWN",
                        "Provider failed after dispatch; billing outcome is unknown"
                    );
                }

                journal["state"] = "rejected";
                journal["http_status"] = (int)response.StatusCode;
                journal["retry_after_seconds"] = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                if (response.Headers.RetryAfter != null)
                {
                    journal["retry_at"] = (
                        response.Headers.RetryAfter.Date
                        ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter.Delta ?? TimeSpan.FromSeconds(60))
                    ).ToString("O");
                }
                store.Journal(key, "rejected", journal);
                throw new ToonException(
                    "PROVIDER_ERROR",
                    response.StatusCode == HttpStatusCode.TooManyRequests
                        ? "Provider rate limit; retry after the recorded Retry-After delay"
                        : "Provider rejected request",
                    retryable: response.StatusCode == HttpStatusCode.TooManyRequests
                );
            }

            journal["state"] = "received";
            store.Journal(key, "received", journal);
            store.ChangeJob(jobId, j => j["provider_usage_confirmed"]!.AsArray().Add(journal.DeepClone()));
            try
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
                var bytes = Convert.FromBase64String(body.Str("audio_base64"));
                J.Require(bytes.Length is > 0 and <= 25 * 1024 * 1024, "PROVIDER_ERROR", "Invalid provider audio size");
                await File.WriteAllBytesAsync(Path.Combine(dir, "speech.partial.mp3"), bytes, ct);
                J.Write(
                    Path.Combine(dir, "provider-result.json"),
                    J.Node(
                        new
                        {
                            sha256 = J.FileHash(Path.Combine(dir, "speech.partial.mp3")),
                            text,
                            voice = voice.DeepClone(),
                            alignment = body["alignment"]?.DeepClone(),
                            normalized_alignment = body["normalized_alignment"]?.DeepClone(),
                        }
                    )
                );
                return await CompleteReceived(journal);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ToonException)
            {
                throw;
            }
            catch
            {
                throw new ToonException(
                    "PROVIDER_ERROR",
                    "Invalid provider response; request journal retained, no automatic retry"
                );
            }
        }

        async Task<AudioInfo> CompleteReceived(JsonNode received)
        {
            var saved = J.Read(Path.Combine(dir, "provider-result.json"));
            var partial = Path.Combine(dir, "speech.partial.mp3");
            var audioPath = File.Exists(partial) ? partial : Path.Combine(dir, "speech.mp3");
            J.Require(
                saved.Str("text") == text
                    && J.Canonical(saved["voice"]!) == J.Canonical(voice)
                    && File.Exists(audioPath)
                    && J.FileHash(audioPath) == saved.Str("sha256"),
                "VERIFY_FAILED",
                "Received audio metadata does not match its request or checksum"
            );
            var prepared = await new Media(config).Prepare(audioPath, null, ct);
            var (alignment, alignmentWarning) = ReadAlignment(
                saved["normalized_alignment"] ?? saved["alignment"],
                text,
                prepared.DurationMs
            );
            if (audioPath == partial)
            {
                File.Move(partial, Path.Combine(dir, "speech.mp3"), true);
            }
            J.Write(
                Path.Combine(dir, "result.json"),
                J.Node(
                    new
                    {
                        sha256 = saved.Str("sha256"),
                        alignment,
                        alignment_warning = alignmentWarning,
                        voice = voice.DeepClone(),
                        text,
                        cache_key = key,
                    }
                )
            );
            received["state"] = "cached";
            store.Journal(key, "cached", received);
            return prepared with { Alignment = alignment };
        }

        void Unknown()
        {
            journal["state"] = "unknown";
            store.Journal(key, "unknown", journal);
            store.ChangeJob(jobId, j => j["provider_usage_unknown"]!.AsArray().Add(journal.DeepClone()));
        }
    }

    public static (JsonNode? Alignment, string? Warning) ReadAlignment(JsonNode? input, string text, double durationMs)
    {
        try
        {
            var alignment = ConvertAlignment(input, text, durationMs);
            return (alignment, alignment is null ? "ALIGNMENT_UNAVAILABLE" : null);
        }
        catch (ToonException error) when (error.Code is "VALIDATION_ERROR" or "ALIGNMENT_UNAVAILABLE")
        {
            // Valid audio remains usable with whole-utterance captions. Never
            // repair unsupported word intervals by guessing or stretching them.
            return (null, error.Code);
        }
    }

    public static JsonNode? ConvertAlignment(JsonNode? input, string text, double durationMs)
    {
        if (input == null)
            return null;

        var chars = input["characters"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        var starts = input["character_start_times_seconds"]!.AsArray();
        var ends = input["character_end_times_seconds"]!.AsArray();
        J.Require(
            chars.Length == starts.Count && chars.Length == ends.Count,
            "ALIGNMENT_UNAVAILABLE",
            "Provider alignment arrays have unequal lengths"
        );
        var spoken = string.Concat(chars);
        J.Require(
            J.Normalize(spoken) == J.Normalize(text),
            "ALIGNMENT_UNAVAILABLE",
            "Provider normalization changed transcript; explicit display mapping is required"
        );
        var tokens = Regex.Matches(spoken, @"\S+");
        var words = new JsonArray();
        var offsets = new List<int>();
        var position = 0;
        foreach (var ch in chars)
        {
            offsets.Add(position);
            position += ch.Length;
        }

        foreach (Match token in tokens)
        {
            var first = offsets.FindIndex(x => x == token.Index);
            var last = offsets.FindLastIndex(x => x < token.Index + token.Length);
            J.Require(first >= 0 && last >= first, "ALIGNMENT_UNAVAILABLE", "Ambiguous Unicode alignment");
            // Punctuation can extend past the decoded audio. Use actual spoken
            // characters for timing, keeping the original token for display.
            var spokenIndices = Enumerable
                .Range(first, last - first + 1)
                .Where(i => chars[i].EnumerateRunes().Any(System.Text.Rune.IsLetterOrDigit))
                .ToArray();
            if (spokenIndices.Length > 0)
            {
                first = spokenIndices[0];
                last = spokenIndices[^1];
            }
            words.Add(
                J.Node(
                    new
                    {
                        index = words.Count,
                        text = token.Value,
                        start_ms = starts[first]!.GetValue<double>() * 1000,
                        end_ms = ends[last]!.GetValue<double>() * 1000,
                    }
                )
            );
        }

        var result = J.Node(
            new
            {
                schema_version = "1.0.0",
                kind = "word_alignment",
                example_only = false,
                source = "provider_alignment",
                locale = "ru-RU",
                normalizer_version = "1.0.0",
                transcript_text = spoken,
                duration_ms = durationMs,
                normalized_words = words,
            }
        );
        Validation.Alignment(result, durationMs);

        return result;
    }
}
