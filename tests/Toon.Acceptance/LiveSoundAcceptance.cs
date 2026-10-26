using System.Text.Json.Nodes;
using Toon.Core;
using Toon.Infrastructure;

namespace Toon.Acceptance;

internal static class LiveSoundAcceptance
{
    public static async Task Run(Config config, int count, string batchKey, string? tapPrompt = null)
    {
        J.Require(count is >= 1 and <= 3, "VALIDATION_ERROR", "Choose 1–3 paid effects, each 2 seconds");
        await using var session = new WorkflowSession(config);
        var requests = new[]
        {
            (
                "Нажатие телефона",
                "A single short dry smartphone touchscreen tap, immediate onset, soft tactile click, no voice, no music, silence after the click."
            ),
            (
                "Уведомление телефона",
                "A short friendly smartphone notification chime, immediate onset, two clean soft notes, no voice, no music, silence after the chime."
            ),
            (
                "Ошибка телефона",
                "A brief comedic smartphone error buzz, immediate onset, two short muted digital pulses, no voice, no music, silence after the buzz."
            ),
        };
        if (tapPrompt != null)
        {
            J.Require(count == 1, "VALIDATION_ERROR", "Custom tap prompt requires count=1");
            requests[0] = ("Нажатие телефона — отчётливый щелчок", tapPrompt);
        }
        var results = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            var (name, prompt) = requests[i];
            var job = await session.Call(
                "start_sound_effect",
                new
                {
                    name,
                    prompt,
                    duration_seconds = 2,
                    allow_new_sfx = true,
                    max_new_sfx_seconds = 2,
                    idempotency_key = batchKey + "-" + i,
                }
            );
            var done = await session.Wait(job.Str("job_id"));
            results.Add(
                J.Node(
                    new
                    {
                        name,
                        job_id = done.Str("job_id"),
                        asset = done["sound_asset"]!.DeepClone(),
                        usage = done["provider_usage_confirmed"]!.DeepClone(),
                        artifacts = await session.Call("get_artifacts", new { job_id = done.Str("job_id") }),
                    }
                )
            );
        }
        var report = Path.Combine(config.Data, "live-sound-acceptance.json");
        J.Write(report, results);
        Console.WriteLine("Live SFX report: " + report);
    }
}
