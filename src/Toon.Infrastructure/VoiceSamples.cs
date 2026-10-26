using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public static class VoiceSamples
{
    public static async Task<JsonNode> Analyze(Config config, Assets assets, JsonNode reference, CancellationToken ct)
    {
        var source = assets.Resolve(reference);
        var audio = await new Media(config).Prepare(source, null, ct);
        var pcm = await File.ReadAllBytesAsync(
            Path.Combine(config.Data, "audio-cache", "recording-" + J.FileHash(source), "audio.pcm"),
            ct
        );
        double energy = 0;
        double peak = 0;
        long clipped = 0;
        long quietWindows = 0;
        var windows = 0;
        for (var start = 0; start < pcm.Length; start += 960 * 2)
        {
            double windowEnergy = 0;
            var end = Math.Min(pcm.Length, start + 960 * 2);
            for (var i = start; i < end; i += 2)
            {
                var value = Math.Abs(BitConverter.ToInt16(pcm, i) / 32768.0);
                peak = Math.Max(peak, value);
                windowEnergy += value * value;
                if (value >= .999)
                {
                    clipped++;
                }
            }
            energy += windowEnergy;
            if (Math.Sqrt(windowEnergy / ((end - start) / 2)) < .01)
            {
                quietWindows++;
            }
            windows++;
        }
        var samples = pcm.Length / 2;
        var rms = Math.Sqrt(energy / samples);
        var silence = quietWindows / (double)windows;
        var clipping = clipped / (double)samples;
        var errors = new JsonArray();
        var warnings = new JsonArray();
        if (audio.DurationMs < 10000)
        {
            errors.Add("Provide at least 10 seconds; 60–120 seconds of clean speech is recommended");
        }

        if (rms < .005 || silence > .85)
        {
            errors.Add("Sample is silent, too quiet or mostly pauses");
        }

        if (clipping > .005)
        {
            errors.Add("Sample contains excessive full-scale clipping");
        }

        if (audio.DurationMs < 60000)
        {
            warnings.Add("Short sample; cloning quality requires listening");
        }
        warnings.Add(
            "Signal measurements cannot confirm speech, one speaker, absence of music/noise, ownership or similarity; listen before uploading"
        );
        return J.Node(
            new
            {
                analysis_version = "pcm-signal-v1",
                source_sha256 = J.FileHash(source),
                duration_ms = audio.DurationMs,
                peak_dbfs = 20 * Math.Log10(Math.Max(peak, 1e-9)),
                rms_dbfs = 20 * Math.Log10(Math.Max(rms, 1e-9)),
                clipped_sample_fraction = clipping,
                quiet_window_fraction = silence,
                technical_ready = errors.Count == 0,
                errors,
                warnings,
                listening_review = "pending_operator",
                speech_recognition = "not_performed",
                original_preserved = true,
            }
        );
    }
}
