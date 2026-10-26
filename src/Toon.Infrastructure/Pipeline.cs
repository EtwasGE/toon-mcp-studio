using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed class Pipeline(Config config)
{
    public async Task<JsonNode> Render(
        JsonNode project,
        string projectId,
        int revision,
        IReadOnlyDictionary<string, AudioInfo> audio,
        Func<JsonNode, string> resolve,
        string directory,
        string quality,
        Action<string> stage,
        CancellationToken ct,
        JsonNode? rigs = null,
        JsonNode? environments = null,
        bool prepareOnly = false
    )
    {
        var media = new Media(config);
        Directory.CreateDirectory(directory);
        var pub = Path.Combine(directory, "public/media");
        Directory.CreateDirectory(pub);
        stage("timing");
        var timeline = Compiler.Compile(project, projectId, revision, audio);
        var assetMap = new JsonObject();
        foreach (var a in audio.Values.DistinctBy(x => x.Hash))
        {
            File.Copy(a.Path, Path.Combine(pub, a.Hash + ".wav"), true);
        }
        foreach (
            var overlay in project
                .Items("scenes")
                .SelectMany(x => x.Items("overlays"))
                .Where(x => x["content"]!.Str("kind") == "image")
        )
        {
            var reference = overlay["content"]!["asset"]!;
            var source = resolve(reference);
            var relative = "media/" + J.FileHash(source) + Path.GetExtension(source).ToLowerInvariant();
            File.Copy(source, Path.Combine(directory, "public", relative), true);
            assetMap[reference.Str("asset_id") + "@" + reference.Str("version")] = relative;
        }

        if (rigs == null)
        {
            rigs = new JsonObject();
            foreach (var character in project.Items("characters"))
            {
                var id = character.Str("character_id");
                J.Require(
                    Validation.Characters.Contains(id),
                    "ASSET_MISSING",
                    "Custom character requires a resolved rig"
                );
                rigs[id] = J.Read(Path.Combine(config.Root, "styles/warm_editorial_2d/characters", id + ".rig.json"));
            }
        }
        foreach (var rig in rigs.AsObject().Select(pair => pair.Value!))
        {
            CharacterRigs.Validate(rig, config.Root);
            foreach (var node in CharacterRigs.Nodes(rig).Where(n => n.Str("type") == "cutout"))
            {
                var reference = node["asset"]!;
                var source = resolve(reference);
                var relative = "media/" + J.FileHash(source) + Path.GetExtension(source).ToLowerInvariant();
                File.Copy(source, Path.Combine(directory, "public", relative), true);
                assetMap[reference.Str("asset_id") + "@" + reference.Str("version")] = relative;
            }
        }
        timeline["rigs"] = rigs.DeepClone();
        if (environments == null)
        {
            environments = new JsonObject();
            foreach (var scene in project.Items("scenes"))
            {
                var reference = scene["background"]!;
                var id = reference.Str("asset_id");
                J.Require(
                    Validation.Backgrounds.Contains(id),
                    "ASSET_MISSING",
                    "Custom environment requires resolved layers"
                );
                environments[id + "@" + reference.Str("version")] = J.Read(
                    Path.Combine(config.Root, "styles/warm_editorial_2d/environments", id + ".environment.json")
                );
            }
        }
        foreach (var environment in environments.AsObject().Select(pair => pair.Value!))
        {
            Environments.Validate(environment, config.Root);
            foreach (var node in Environments.Nodes(environment).Where(n => n.Str("type") == "cutout"))
            {
                var reference = node["asset"]!;
                var source = resolve(reference);
                var relative = "media/" + J.FileHash(source) + Path.GetExtension(source).ToLowerInvariant();
                File.Copy(source, Path.Combine(directory, "public", relative), true);
                assetMap[reference.Str("asset_id") + "@" + reference.Str("version")] = relative;
            }
        }
        timeline["environments"] = environments.DeepClone();
        timeline["assets"] = assetMap;
        J.Write(Path.Combine(directory, "timeline.json"), timeline);
        J.Write(Path.Combine(directory, "project.json"), project);
        J.Write(Path.Combine(directory, "captions.json"), timeline["captions"]!);
        var srt = new StringBuilder();
        var captionIndex = 1;

        string Time(int frame) =>
            TimeSpan.FromSeconds(frame / 30.0).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);

        foreach (var c in timeline.Items("captions"))
        {
            srt.AppendLine((captionIndex++).ToString())
                .AppendLine(Time(c.Int("start_frame")) + " --> " + Time(c.Int("end_frame_exclusive")))
                .AppendLine(c.Str("text"))
                .AppendLine();
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "captions.srt"), srt.ToString(), ct);
        await media.Mix(timeline, audio, project, resolve, Path.Combine(directory, "mix.wav"), ct);
        if (prepareOnly)
        {
            File.Copy(Path.Combine(directory, "mix.wav"), Path.Combine(directory, "public", "studio-mix.wav"), true);
            J.Write(
                Path.Combine(directory, "studio-props.json"),
                J.Node(new { timeline, studioAudio = "studio-mix.wav" })
            );
            return J.Node(
                new
                {
                    kind = "studio_preview",
                    project_id = projectId,
                    revision,
                    duration_seconds = timeline.Int("total_frames") / 30.0,
                    video_exported = false,
                    audio = "Prepared mix; final loudness normalization is applied on MP4 export",
                    human_review = "pending",
                }
            );
        }
        stage("render");
        await Processes.Run(
            config.Node,
            [
                Path.Combine(config.Root, "renderer/render.mjs"),
                Path.Combine(directory, "timeline.json"),
                directory,
                quality,
            ],
            config.Root,
            ct,
            Path.Combine(directory, "renderer.log")
        );
        stage("encode");
        var loud = await media.Loudness(Path.Combine(directory, "mix.wav"), ct);
        var filter =
            $"loudnorm=I=-16:TP=-1.5:LRA=11:measured_I={loud.Str("input_i")}:measured_TP={loud.Str("input_tp")}:measured_LRA={loud.Str("input_lra")}:measured_thresh={loud.Str("input_thresh")}:offset={loud.Str("target_offset")}:linear=true";
        var partial = Path.Combine(directory, "video.partial.mp4");
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
                "-vf",
                "scale=in_range=pc:out_range=tv",
                "-c:v",
                "libx264",
                "-preset",
                "fast",
                "-crf",
                "20",
                "-pix_fmt",
                "yuv420p",
                "-color_range",
                "tv",
                "-af",
                filter,
                "-c:a",
                "aac",
                "-ar",
                "48000",
                "-b:a",
                "192k",
                "-movflags",
                "+faststart",
                partial,
            ],
            config.Root,
            ct,
            Path.Combine(directory, "encode.log")
        );
        stage("verify");
        var report = await media.Verify(partial, timeline, quality == "preview", ct);
        J.Write(Path.Combine(directory, "validation-report.json"), report);
        File.Move(partial, Path.Combine(directory, "video.mp4"), true);
        if (quality == "final")
        {
            var export = Path.Combine(directory, "export");
            Directory.CreateDirectory(export);
            foreach (
                var file in new[]
                {
                    "project.json",
                    "timeline.json",
                    "captions.json",
                    "captions.srt",
                    "validation-report.json",
                }
            )
            {
                File.Copy(Path.Combine(directory, file), Path.Combine(export, file), true);
            }
            var assets = Path.Combine(export, "media");
            Directory.CreateDirectory(assets);
            foreach (var file in Directory.GetFiles(pub))
            {
                File.Copy(file, Path.Combine(assets, Path.GetFileName(file)), true);
            }
            if (project["music"] != null)
            {
                var source = resolve(project["music"]!["asset"]!);
                File.Copy(source, Path.Combine(assets, J.FileHash(source) + Path.GetExtension(source)), true);
            }

            var dependencies = Directory
                .GetFiles(assets)
                .Select(x => new
                {
                    path = "media/" + Path.GetFileName(x),
                    sha256 = J.FileHash(x),
                    bytes = new FileInfo(x).Length,
                })
                .ToArray();
            J.Write(
                Path.Combine(export, "manifest.json"),
                J.Node(
                    new
                    {
                        schema_version = "1.0.0",
                        renderer_version = "1.0.0",
                        project_id = projectId,
                        revision,
                        dependencies,
                        voice_aliases_require_local_configuration = true,
                        original_asset_references = project
                            .Items("scenes")
                            .SelectMany(s => s.Items("utterances"))
                            .Select(u => new
                            {
                                utterance_id = u.Str("utterance_id"),
                                audio = u["audio"]!.DeepClone(),
                                prepared_file = "media/" + audio[u.Str("utterance_id")].Hash + ".wav",
                                alignment = audio[u.Str("utterance_id")].Alignment?.DeepClone(),
                            })
                            .ToArray(),
                    }
                )
            );
            var zip = Path.Combine(directory, "project.zip");
            if (File.Exists(zip))
            {
                File.Delete(zip);
            }
            ZipFile.CreateFromDirectory(export, zip);
        }

        return report;
    }
}
