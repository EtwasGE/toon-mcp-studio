using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed class Media(Config config)
{
    public async Task<JsonNode> Probe(string path, CancellationToken ct) =>
        JsonNode.Parse(
            await Processes.Run(
                config.Ffprobe,
                ["-v", "error", "-show_format", "-show_streams", "-of", "json", path],
                config.Root,
                ct
            )
        )!;

    public async Task<AudioInfo> Prepare(string source, JsonNode? alignment, CancellationToken ct)
    {
        var hash = J.FileHash(source);
        var dir = Path.Combine(config.Data, "audio-cache", "recording-" + hash);
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, "audio.wav");
        var raw = Path.Combine(dir, "audio.pcm");
        var analysisPath = Path.Combine(dir, "analysis.json");
        var validCache = File.Exists(wav) && File.Exists(raw) && File.Exists(analysisPath);
        if (validCache)
        {
            try
            {
                var cached = J.Read(analysisPath);
                validCache =
                    cached["pcm_sha256"]?.ToString() == J.FileHash(raw)
                    && cached["wav_sha256"]?.ToString() == J.FileHash(wav);
            }
            catch
            {
                validCache = false;
            }
        }

        if (!validCache)
        {
            var tmp = wav + ".partial.wav";
            await Processes.Run(
                config.Ffmpeg,
                [
                    "-v",
                    "error",
                    "-y",
                    "-i",
                    source,
                    "-map",
                    "0:a:0",
                    "-vn",
                    "-ac",
                    "1",
                    "-ar",
                    "48000",
                    "-t",
                    (config.MaxAudioSeconds + .1).ToString(CultureInfo.InvariantCulture),
                    "-c:a",
                    "pcm_s16le",
                    tmp,
                ],
                config.Root,
                ct
            );
            await Processes.Run(
                config.Ffmpeg,
                ["-v", "error", "-y", "-i", tmp, "-f", "s16le", raw + ".partial"],
                config.Root,
                ct
            );
            File.Move(raw + ".partial", raw, true);
            File.Move(tmp, wav, true);
        }

        var bytes = await File.ReadAllBytesAsync(raw, ct);
        long samples = bytes.Length / 2;
        J.Require(
            samples > 0 && samples <= config.MaxAudioSeconds * 48000L,
            "VALIDATION_ERROR",
            $"Audio must contain 0–{config.MaxAudioSeconds} seconds of decodable samples"
        );
        if (alignment?["normalized_words"] != null)
        {
            Validation.Alignment(alignment, samples / 48.0);
        }
        var envelope = new List<double>();
        double smoothed = 0;
        bool opened = false;
        for (long i = 0; i < samples; i += 1600)
        {
            double sum = 0;
            var count = Math.Min(1600, samples - i);
            for (long j = 0; j < count; j++)
            {
                var v = BitConverter.ToInt16(bytes, (int)((i + j) * 2)) / 32768.0;
                sum += v * v;
            }

            var rms = Math.Sqrt(sum / count);
            smoothed = rms > smoothed ? .75 * rms + .25 * smoothed : .5 * rms + .5 * smoothed;
            opened = opened ? smoothed > .008 : smoothed > .015;
            envelope.Add(opened ? Math.Clamp(smoothed * 12, .1, 1) : 0);
        }

        J.Write(
            analysisPath,
            J.Node(
                new
                {
                    samples,
                    envelope,
                    mode = "approximate/amplitude",
                    pcm_sha256 = J.FileHash(raw),
                    wav_sha256 = J.FileHash(wav),
                    preprocessing_version = "pcm48-mono-v1",
                }
            )
        );

        return new AudioInfo(wav, J.FileHash(wav), samples, alignment, envelope.ToArray());
    }

    public async Task<(AudioInfo Audio, int TrimmedSamples)> PreparePropSound(string source, CancellationToken ct)
    {
        var prepared = await Prepare(source, null, ct);
        var pcm = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(prepared.Path)!, "audio.pcm"), ct);
        var peak = 0;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)));
        }

        J.Require(peak >= 64, "SFX_TOO_QUIET", "Prop sound has no audible contact");
        var threshold = Math.Max(64, peak * 0.02);
        var onset = 0;
        while (onset < pcm.Length / 2 && Math.Abs((int)BitConverter.ToInt16(pcm, onset * 2)) < threshold)
        {
            onset++;
        }

        // Keep a 2 ms lead-in to avoid cutting the transient. Never change the source asset.
        var trim = Math.Max(0, onset - 96);
        if (trim == 0)
            return (prepared, 0);

        var directory = Path.Combine(config.Data, "audio-cache", "prop-onset-v1-" + prepared.Hash);
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "audio.wav");
        await Processes.Run(
            config.Ffmpeg,
            [
                "-v",
                "error",
                "-y",
                "-i",
                prepared.Path,
                "-af",
                $"atrim=start_sample={trim},asetpts=PTS-STARTPTS",
                "-c:a",
                "pcm_s16le",
                output,
            ],
            config.Root,
            ct
        );
        return (await Prepare(output, null, ct), trim);
    }

    public async Task Mix(
        JsonNode timeline,
        IReadOnlyDictionary<string, AudioInfo> audio,
        JsonNode project,
        Func<JsonNode, string> resolve,
        string output,
        CancellationToken ct
    )
    {
        var total = timeline.Int("total_frames") / 30.0;
        var args = new List<string> { "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=mono" };
        var filters = new List<string>();
        var labels = new List<string> { "[0:a]" };
        var index = 1;
        foreach (var u in timeline.Items("utterances"))
        {
            args.AddRange(["-i", audio[u.Str("utterance_id")].Path]);
            filters.Add($"[{index}:a]adelay={u["start_sample"]}S:all=1[a{index}]");
            labels.Add($"[a{index}]");
            index++;
        }

        filters.Add(string.Concat(labels) + $"amix=inputs={labels.Count}:normalize=0:duration=first[voice]");
        var last = "voice";
        if (project["music"] is JsonNode music)
        {
            args.AddRange(["-stream_loop", "-1", "-i", resolve(music["asset"]!)]);
            filters.Add(
                $"[{index}:a]aformat=sample_rates=48000:channel_layouts=mono,volume={music.Num("gain_db").ToString(CultureInfo.InvariantCulture)}dB[music]"
            );
            filters.Add("[voice]asplit[side][dry]");
            filters.Add("[music][side]sidechaincompress=threshold=0.015:ratio=8:attack=20:release=300[ducked]");
            filters.Add("[dry][ducked]amix=inputs=2:normalize=0[mixed]");
            last = "mixed";
            index++;
        }

        var effects = new List<string> { $"[{last}]" };
        foreach (var sound in timeline.Items("sounds"))
        {
            var prepared = await Prepare(resolve(sound["asset"]!), null, ct);
            args.AddRange(["-i", prepared.Path]);
            filters.Add(
                $"[{index}:a]atrim=start_sample={sound.Int("onset_trim_samples")},asetpts=PTS-STARTPTS,volume={sound.Num("gain_db").ToString(CultureInfo.InvariantCulture)}dB,adelay={sound["start_sample"]}S:all=1[sfx{index}]"
            );
            effects.Add($"[sfx{index}]");
            index++;
        }

        if (effects.Count > 1)
        {
            filters.Add(string.Concat(effects) + $"amix=inputs={effects.Count}:normalize=0:duration=first[effects]");
            last = "effects";
        }

        filters.Add($"[{last}]atrim=duration={total.ToString(CultureInfo.InvariantCulture)}[out]");
        args.AddRange([
            "-filter_complex",
            string.Join(';', filters),
            "-map",
            "[out]",
            "-t",
            total.ToString(CultureInfo.InvariantCulture),
            "-c:a",
            "pcm_s16le",
            output,
        ]);
        await Processes.Run(config.Ffmpeg, args, config.Root, ct);
    }

    public async Task<JsonNode> Loudness(string path, CancellationToken ct)
    {
        var result = await Processes.Run(
            config.Ffmpeg,
            ["-hide_banner", "-i", path, "-af", "loudnorm=I=-16:TP=-1:LRA=11:print_format=json", "-f", "null", "-"],
            config.Root,
            ct
        );
        var match = Regex.Match(result, @"\{\s*""input_i""[\s\S]*?\}");
        J.Require(match.Success, "VERIFY_FAILED", "Loudness measurement unavailable");

        return JsonNode.Parse(match.Value)!;
    }

    public async Task<JsonNode> Verify(string path, JsonNode timeline, bool preview, CancellationToken ct)
    {
        var probe = await Probe(path, ct);
        var streams = probe.Items("streams").ToArray();
        var v = streams.FirstOrDefault(x => x.Str("codec_type") == "video");
        var a = streams.FirstOrDefault(x => x.Str("codec_type") == "audio");
        J.Require(v != null && a != null, "VERIFY_FAILED", "Required audio/video stream missing");
        J.Require(
            v!.Str("codec_name") == "h264"
                && v.Str("pix_fmt") == "yuv420p"
                && v.Int("width") == (preview ? 540 : 1080)
                && v.Int("height") == (preview ? 960 : 1920)
                && v.Str("r_frame_rate") == "30/1"
                && a!.Str("codec_name") == "aac"
                && a.Str("sample_rate") == "48000",
            "VERIFY_FAILED",
            "Unexpected stream format"
        );
        var duration = double.Parse(v.Str("duration"), CultureInfo.InvariantCulture);
        J.Require(
            Math.Abs(duration - timeline.Int("total_frames") / 30.0) <= 1.0 / 30,
            "VERIFY_FAILED",
            "Video duration differs from timeline"
        );
        await Processes.Run(
            config.Ffmpeg,
            ["-v", "error", "-xerror", "-i", path, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"],
            config.Root,
            ct
        );
        var loudness = await Loudness(path, ct);
        var integrated = double.Parse(loudness.Str("input_i"), CultureInfo.InvariantCulture);
        var peak = double.Parse(loudness.Str("input_tp"), CultureInfo.InvariantCulture);
        // Sparse effects have a wider crest factor than speech. Keep the peak ceiling,
        // but do not force quiet gaps and short transients to speech loudness.
        var effectsOnly = !timeline.Items("utterances").Any() && timeline.Items("sounds").Any();
        var loudnessValid = effectsOnly ? integrated is >= -30 and <= -15 : Math.Abs(integrated + 16) <= 1;
        J.Require(
            double.IsFinite(integrated) && loudnessValid && peak <= -1,
            "VERIFY_FAILED",
            $"Loudness outside limits: {integrated} LUFS, {peak} dBTP"
        );

        return J.Node(
            new
            {
                verified = true,
                full_decode = true,
                duration_seconds = duration,
                loudness_lufs = integrated,
                loudness_profile = effectsOnly ? "effects-only" : "speech",
                true_peak_dbtp = peak,
                streams = probe["streams"]!.DeepClone(),
                mouth_mode = "approximate/amplitude",
                human_listening = "pending",
                phone_review = "pending",
            }
        );
    }
}
