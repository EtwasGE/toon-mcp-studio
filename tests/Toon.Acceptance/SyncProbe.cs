using System.Text.Json.Nodes;
using Toon.Core;
using Toon.Infrastructure;

public static class SyncProbe
{
    public static async Task Run(Config config, string directory)
    {
        Directory.CreateDirectory(directory);
        var pulse = Path.Combine(directory, "pulse-test-fixture.wav");
        await Processes.Run(
            config.Ffmpeg,
            [
                "-v",
                "error",
                "-y",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=1000:sample_rate=48000:duration=0.1",
                "-c:a",
                "pcm_s16le",
                pulse,
            ],
            config.Root,
            CancellationToken.None
        );
        var prepared = await new Media(config).Prepare(pulse, null, CancellationToken.None);
        var draft = J.Read(Path.Combine(config.Root, "examples/offline-dialogue/project.json"));
        draft["captions"]!["enabled"] = false;
        draft["scenes"] = JsonNode.Parse(
            """
            [{"scene_id":"sync","kind":"dialogue","background":{"asset_id":"neutral_dark","version":"1.0.0"},"camera":{"shot":"screen"},"cast":[],"lead_in_ms":1000,"tail_ms":900,"utterances":[{"utterance_id":"pulse","speaker_character_id":"buyer","text":"Технический импульс, не речь","audio":{"mode":"recording","asset":{"asset_id":"test_pulse","version":"1.0.0"}},"pause_after_ms":0}],"events":[],"overlays":[{"overlay_id":"flash","start":{"kind":"utterance_start","utterance_id":"pulse"},"end":{"kind":"scene_end"},"content":{"kind":"image","asset":{"asset_id":"screen","version":"1.0.0"},"placement":"full_frame","fit":"contain","render_mode":"exact"}}]}]
            """
        );
        var audio = new Dictionary<string, AudioInfo> { { "pulse", prepared } };
        var timeline = Compiler.Compile(draft, "sync_fixture", 1, audio);
        var pub = Path.Combine(directory, "public/media");
        Directory.CreateDirectory(pub);
        File.Copy(
            Path.Combine(config.Root, "examples/offline-dialogue/screen.png"),
            Path.Combine(pub, "screen.png"),
            true
        );
        timeline["assets"] = J.Node(new Dictionary<string, string> { { "screen@1.0.0", "media/screen.png" } });
        J.Write(Path.Combine(directory, "timeline.json"), timeline);
        await new Media(config).Mix(
            timeline,
            audio,
            draft,
            _ => throw new Exception(),
            Path.Combine(directory, "mix.wav"),
            CancellationToken.None
        );
        await Processes.Run(
            config.Node,
            [
                Path.Combine(config.Root, "renderer/render.mjs"),
                Path.Combine(directory, "timeline.json"),
                directory,
                "preview",
            ],
            config.Root,
            CancellationToken.None,
            Path.Combine(directory, "renderer.log")
        );
        var video = Path.Combine(directory, "sync-test-fixture.mp4");
        await Processes.Run(
            config.Ffmpeg,
            [
                "-v",
                "error",
                "-y",
                "-i",
                Path.Combine(directory, "silent.partial.mp4"),
                "-i",
                Path.Combine(directory, "mix.wav"),
                "-map",
                "0:v:0",
                "-map",
                "1:a:0",
                "-c:v",
                "copy",
                "-c:a",
                "aac",
                "-ar",
                "48000",
                video,
            ],
            config.Root,
            CancellationToken.None
        );
        var pcm = Path.Combine(directory, "decoded.pcm");
        var pixels = Path.Combine(directory, "pixels.rgb");
        await Processes.Run(
            config.Ffmpeg,
            ["-v", "error", "-y", "-i", video, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "s16le", pcm],
            config.Root,
            CancellationToken.None
        );
        await Processes.Run(
            config.Ffmpeg,
            [
                "-v",
                "error",
                "-y",
                "-i",
                video,
                "-map",
                "0:v:0",
                "-vf",
                "crop=2:2:270:480",
                "-pix_fmt",
                "rgb24",
                "-f",
                "rawvideo",
                pixels,
            ],
            config.Root,
            CancellationToken.None
        );
        var bytes = await File.ReadAllBytesAsync(pcm);
        var samples = bytes.Length / 2;
        var onset = -1;
        for (var i = 0; i < samples - 240; i += 240)
        {
            double sum = 0;
            for (var j = 0; j < 240; j++)
            {
                var value = BitConverter.ToInt16(bytes, (i + j) * 2) / 32768.0;
                sum += value * value;
            }

            if (Math.Sqrt(sum / 240) > .02)
            {
                onset = i;
                break;
            }
        }

        var rgb = await File.ReadAllBytesAsync(pixels);
        var visible = -1;
        for (var frame = 0; frame < rgb.Length / 12; frame++)
        {
            if (rgb[frame * 12] + rgb[frame * 12 + 1] + rgb[frame * 12 + 2] > 300)
            {
                visible = frame;
                break;
            }
        }

        J.Require(
            onset >= 0 && visible == 30 && Math.Abs(onset / 1600.0 - visible) <= 1,
            "VERIFY_FAILED",
            $"Synchronization failed: audio sample {onset}, visual frame {visible}"
        );
        J.Write(
            Path.Combine(directory, "sync-report.json"),
            J.Node(
                new
                {
                    test_fixture = true,
                    not_speech = true,
                    audio_onset_sample = onset,
                    visual_onset_frame = visible,
                    offset_frames = onset / 1600.0 - visible,
                    within_one_frame = true,
                }
            )
        );
    }
}
