using System.Text.Json.Nodes;
using Toon.Core;
using Toon.Infrastructure;

namespace Toon.Acceptance;

internal static class PropAcceptance
{
    public static async Task Run(Config original)
    {
        Environment.SetEnvironmentVariable("TOON_IMPORT_ROOTS", original.Data);
        var config = new Config();
        var studio = new Studio(config);
        var source = Path.Combine(config.Data, "prop-test.wav");
        await Processes.Run(
            config.Ffmpeg,
            ["-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=700:duration=0.5", source],
            config.Root,
            CancellationToken.None
        );
        var asset = await studio.Assets.Register(source, "audio", null, CancellationToken.None);
        var reference = J.Node(new { asset_id = asset.Str("asset_id"), version = asset.Str("version") });
        foreach (var definition in Props.Definitions)
        {
            foreach (var action in definition.Actions)
            {
                studio.Props.Bind(definition.Id, action, reference, 0);
            }
        }
        Check(studio.Catalog("props").AsArray().Count == 5, "five props in catalog");
        var project = J.Read(Path.Combine(config.Root, "examples/offline-dialogue/project.json"));
        project["captions"]!["enabled"] = false;
        var scenes = new JsonArray();
        foreach (var definition in Props.Definitions)
        {
            scenes.Add(
                J.Node(
                    new
                    {
                        scene_id = definition.Id,
                        kind = "hold",
                        hold_ms = 4000,
                        background = new { asset_id = "neutral_dark", version = "1.0.0" },
                        camera = new { shot = "wide" },
                        cast = new[]
                        {
                            new
                            {
                                character_id = "buyer",
                                placement = "center",
                                pose_id = "seated",
                                emotion_id = "neutral",
                            },
                        },
                        props = new[]
                        {
                            new
                            {
                                instance_id = "held",
                                prop_id = definition.Id,
                                version = "1.0.0",
                                character_id = "buyer",
                                hand = "right",
                            },
                        },
                        events = new[]
                        {
                            new
                            {
                                event_id = definition.Id + "_use",
                                at = new { kind = "scene_start", offset_ms = 0 },
                                action = new
                                {
                                    kind = "prop",
                                    instance_id = "held",
                                    prop_action = definition.Actions[0],
                                },
                            },
                        },
                        overlays = new JsonArray(),
                    }
                )
            );
        }
        project["scenes"] = scenes;
        var saved = studio.Create(project);
        var pinned = studio.Store.Project(saved.Str("project_id"), 1)["project"]!;
        Check(project["scenes"]![0]!["events"]![0]!["sound"] == null, "caller document unchanged");
        var audio = new Dictionary<string, AudioInfo>();
        var timeline = Compiler.Compile(pinned, "props-test", 1, audio);
        Check(timeline.Items("sounds").Count() == 5, "one sound per action");
        Check(timeline["sounds"]![0]!["start_sample"]!.GetValue<long>() == 18 * 1600L, "exact contact sample");
        Check(timeline["scenes"]![0]!["props"]!.AsArray().Count == 1, "prop instances survive compilation");
        var first = pinned["scenes"]![0]!;
        var duplicate = pinned.DeepClone();
        duplicate["scenes"]![0]!["props"]!.AsArray().Add(first["props"]![0]!.DeepClone());
        Error(() => studio.Create(duplicate), "VALIDATION_ERROR");
        var absent = pinned.DeepClone();
        absent["scenes"]![0]!["events"]![0]!["action"]!["instance_id"] = "absent";
        Error(() => studio.Create(absent), "VALIDATION_ERROR");
        var unsupported = pinned.DeepClone();
        unsupported["scenes"]![0]!["events"]![0]!["action"]!["prop_action"] = "puff";
        Error(() => studio.Create(unsupported), "UNSUPPORTED_ACTION");
        var unknownVersion = pinned.DeepClone();
        unknownVersion["scenes"]![0]!["props"]![0]!["version"] = "9.0.0";
        Error(() => studio.Create(unknownVersion), "VALIDATION_ERROR");
        var silent = pinned.DeepClone();
        silent["scenes"]![0]!["events"]![0]!.AsObject().Remove("sound");
        Error(() => studio.CheckDraft(silent), "ASSET_MISSING");
        var overlap = pinned.DeepClone();
        var extra = first["events"]![0]!.DeepClone();
        extra["event_id"] = "overlap";
        extra["at"]!["offset_ms"] = 100;
        overlap["scenes"]![0]!["events"]!.AsArray().Add(extra);
        Error(() => Compiler.Compile(overlap, "props-test", 1, audio), "AMBIGUOUS_CUE");
        var late = pinned.DeepClone();
        late["scenes"]![0]!["events"]![0]!["at"]!["offset_ms"] = 2000;
        Error(() => Compiler.Compile(late, "props-test", 1, audio), "VALIDATION_ERROR");
        Error(() => studio.Props.Bind("phone", "tap", reference, 0), "REVISION_CONFLICT");
        studio.Props.Bind("phone", "tap", reference, 1, -6);
        Check(
            pinned["scenes"]![0]!["events"]![0]!["sound"]!.Num("gain_db") == -12,
            "binding edits preserve saved projects"
        );
        var directory = Path.Combine(config.Data, "props-preview");
        await new Pipeline(config).Render(
            pinned,
            saved.Str("project_id"),
            1,
            audio,
            studio.Assets.Resolve,
            directory,
            "preview",
            _ => { },
            CancellationToken.None,
            prepareOnly: true
        );
        var pcm = Path.Combine(config.Data, "props.pcm");
        await Processes.Run(
            config.Ffmpeg,
            ["-v", "error", "-y", "-i", Path.Combine(directory, "mix.wav"), "-f", "s16le", pcm],
            config.Root,
            CancellationToken.None
        );
        var bytes = await File.ReadAllBytesAsync(pcm);
        Check(bytes.Take(18 * 1600 * 2).All(b => b == 0), "PCM silent before contact");
        Check(bytes.Skip(18 * 1600 * 2).Take(4800).Any(b => b != 0), "PCM audible at contact");
        Console.WriteLine("Prop acceptance completed: " + directory);
    }

    private static void Check(bool ok, string name)
    {
        J.Require(ok, "TEST_FAILED", name);
        Console.WriteLine("PASS props " + name);
    }

    private static void Error(Action action, string code)
    {
        try
        {
            action();
        }
        catch (ToonException ex) when (ex.Code == code)
        {
            Check(true, code);
            return;
        }
        throw new Exception("Expected " + code);
    }
}
