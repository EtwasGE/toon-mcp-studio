using System.Text.Json.Nodes;
using Toon.Core;
using Toon.Infrastructure;

namespace Toon.Acceptance;

// Explicit paid opt-in only. Never called by the default/offline acceptance suite.
public static class LiveSpeechAcceptance
{
    public static async Task Run(Config config, int limit)
    {
        J.Require(limit is > 0 and <= 1000, "TTS_LIMIT_EXCEEDED", "Live check is capped at 1000 characters");
        var secret = Secrets.ElevenLabsApiKey();
        J.Require(!string.IsNullOrWhiteSpace(secret), "VOICE_NOT_CONFIGURED", "Configure ElevenLabs credentials first");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
        request.Headers.Add("xi-api-key", secret);
        using var response = await http.SendAsync(request);
        J.Require(
            response.IsSuccessStatusCode,
            "PROVIDER_ERROR",
            $"Voice metadata request failed (HTTP {(int)response.StatusCode}); no synthesis requested"
        );
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var available = body.Items("voices")
            .Where(voice => voice.Str("category") == "premade" && voice["labels"]?["gender"]?.ToString() == "male")
            .ToArray();
        J.Require(
            available.Length >= 2,
            "VOICE_NOT_CONFIGURED",
            "Live check requires two available premade male voices for the example cast; no clone is created"
        );
        var selected = available
            .OrderByDescending(voice => voice["labels"]?["language"]?.ToString() == "ru")
            .ThenBy(voice => voice.Str("name"), StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        var aliases = new JsonObject();
        for (var index = 0; index < selected.Length; index++)
        {
            aliases["live_" + index] = J.Node(
                new
                {
                    provider = "elevenlabs",
                    voice_id = selected[index].Str("voice_id"),
                    model_id = Speech.DefaultModel,
                    voice_settings = new
                    {
                        stability = .5,
                        similarity_boost = .75,
                        style = 0,
                        use_speaker_boost = true,
                    },
                }
            );
        }
        Environment.SetEnvironmentVariable("TOON_VOICE_ALIASES", aliases.ToJsonString());
        Environment.SetEnvironmentVariable(
            "TOON_MAX_TTS_CHARACTERS",
            limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        var studio = new Studio(config);
        var project = J.Read(Path.Combine(config.Root, "examples/offline-dialogue/project.json"));
        project["title"] = "Живая проверка ElevenLabs — готовые голоса, учебный интерфейс";
        foreach (var overlay in project.Items("scenes").SelectMany(scene => scene.Items("overlays")))
        {
            if (overlay["content"]?["text"] is JsonNode label)
            {
                overlay["content"]!["text"] = label.ToString().Replace("TOON · OFFLINE DEMO", "TOON · ELEVENLABS DEMO");
            }
        }
        foreach (var line in project.Items("scenes").SelectMany(scene => scene.Items("utterances")))
        {
            line["audio"] = J.Node(
                new
                {
                    mode = "tts",
                    voice_alias = line.Str("speaker_character_id") == "buyer" ? "live_0" : "live_1",
                    delivery = "neutral",
                }
            );
        }
        var screen = await studio.Assets.Register(
            Path.Combine(config.Root, "examples/offline-dialogue/screen.png"),
            "image",
            null,
            CancellationToken.None
        );
        project["scenes"]![1]!["overlays"]![0]!["content"]!["asset"] = J.Node(
            new { asset_id = screen.Str("asset_id"), version = screen.Str("version") }
        );
        var saved = studio.Create(project);
        var estimated = saved["readiness"]!.Int("estimated_new_tts_characters");
        J.Require(
            estimated <= limit,
            "TTS_LIMIT_EXCEEDED",
            "Project exceeds the explicitly authorized live check budget"
        );
        var job = studio.Start(
            saved.Str("project_id"),
            saved.Int("revision"),
            "final",
            "live-tts-" + saved.Str("project_id"),
            true,
            limit
        );
        Console.WriteLine($"LIVE TTS: at most {estimated} new characters, authorized cap {limit}; no clone requests");
        var authorization = J.Node(
            new
            {
                project_id = saved.Str("project_id"),
                job_id = job.Str("job_id"),
                max_new_tts_characters = limit,
                estimated_new_tts_characters = estimated,
                voices = selected.Select(voice => new
                {
                    voice_id = voice.Str("voice_id"),
                    name = voice.Str("name"),
                    category = "premade",
                }),
                cloned = false,
            }
        );
        J.Write(Path.Combine(config.Data, "live-tts-authorization.json"), authorization);
        J.Write(Path.Combine(config.Data, "live-tts-authorization-" + job.Str("job_id") + ".json"), authorization);
        await new Worker(studio).Execute(studio.Store.Claim()!, CancellationToken.None);
        var completed = studio.Job(job.Str("job_id"));
        J.Write(
            Path.Combine(config.Data, "live-tts-report.json"),
            J.Node(
                new
                {
                    project_id = saved.Str("project_id"),
                    job_id = job.Str("job_id"),
                    state = completed.Str("state"),
                    error = completed["error"]?.DeepClone(),
                    estimated_new_tts_characters = estimated,
                    authorized_character_limit = limit,
                    provider_billing_units = (int?)null,
                    live_clone = "pending user sample, consent and separate clone authorization",
                    human_listening = "pending",
                    artifacts = completed["artifacts"]!.DeepClone(),
                }
            )
        );
        Console.WriteLine("REPORT " + Path.Combine(config.Data, "live-tts-report.json"));
        J.Require(
            completed.Str("state") == "succeeded",
            "VERIFY_FAILED",
            "Live check did not succeed; inspect report. Do not automatically repeat paid requests"
        );
    }
}
