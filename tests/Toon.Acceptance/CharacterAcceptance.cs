using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using Toon.Core;
using Toon.Infrastructure;

namespace Toon.Acceptance;

public static class CharacterAcceptance
{
    public static async Task Run(Config config)
    {
        var checks = new JsonArray();
        var studio = new Studio(config);

        async Task<McpClient> Connect() =>
            await McpClient.CreateAsync(
                new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = "Character acceptance",
                        Command = "dotnet",
                        Arguments = [Path.Combine(config.Root, "src/Toon.Mcp/bin/Debug/net10.0/Toon.Mcp.dll")],
                        WorkingDirectory = config.Root,
                    }
                ),
                new McpClientOptions
                {
                    ProtocolVersion = "2026-07-28",
                    DiscoverProbeTimeout = TimeSpan.FromSeconds(60),
                    InitializationTimeout = TimeSpan.FromSeconds(90),
                }
            );

        async Task<JsonNode> Call(McpClient client, string name, object arguments, string? error = null)
        {
            var result = await client.CallToolAsync(
                name,
                J.Node(arguments).AsObject().ToDictionary(p => p.Key, p => (object?)p.Value)
            );
            var body = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
            if (error != null)
            {
                Check(result.IsError == true && body["error"]!.Str("code") == error, "MCP " + error);
                return body["error"]!;
            }

            if (result.IsError == true)
                throw new Exception(body.ToJsonString());
            return body["data"]!;
        }
        async Task<JsonNode> Wait(string id)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromMinutes(8))
            {
                var job = studio.Job(id);
                if (job.Str("state") == "succeeded")
                    return job;

                if (job.Str("state") is "failed" or "cancelled")
                    throw new Exception(job.ToJsonString());
                await Task.Delay(250);
            }
            throw new TimeoutException("Character worker timed out");
        }
        foreach (var id in Validation.Characters)
        {
            var rig = studio.Library.ResolveCharacter(id, "1.0.0");
            Check(CharacterRigs.Readiness(rig).Count == 0, "builtin rig complete: " + id);
        }
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = config.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add(Path.Combine(config.Root, "src/Toon.Worker/bin/Debug/net10.0/Toon.Worker.dll"));
        using var worker = Process.Start(info)!;
        var stderr = worker.StandardError.ReadToEndAsync();
        var stdout = worker.StandardOutput.ReadToEndAsync();
        try
        {
            await using var client = await Connect();
            var template = await Call(client, "get_character_recipe_template", new { });
            Validation.Schema(template, Path.Combine(config.Root, "contracts/character-recipe.schema.json"));
            var submission = SubmissionTemplates.Get(config, "character")["submission"]!.DeepClone();
            submission["display_name"] = "Тестовый оригинальный персонаж";
            submission["rights_confirmed"] = true;
            var item = await Call(client, "create_library_item", new { submission });
            var itemId = item.Str("item_id");
            var source = Path.Combine(config.Root, "examples/character-cutout/sheet.png");
            foreach (var role in new[] { "front", "three_quarter" })
            {
                item = await Call(
                    client,
                    "import_library_material",
                    new
                    {
                        item_id = itemId,
                        expected_revision = item.Int("revision"),
                        role,
                        source_path = source,
                        note = "Original synthetic cutout test sheet, not a real submitted sketch or three-quarter view",
                    }
                );
            }
            var bad = template.DeepClone();
            bad["rig"]!["layers"]![0]!["props"] = J.Node(new { onload = "untrusted" });
            await Call(
                client,
                "set_character_recipe",
                new
                {
                    item_id = itemId,
                    expected_revision = item.Int("revision"),
                    recipe = bad,
                },
                "VALIDATION_ERROR"
            );
            bad = template.DeepClone();
            bad["rig"]!["layers"]![0]!["asset"] = J.Node(new { asset_id = "other", version = "1.0.0" });
            await Call(
                client,
                "set_character_recipe",
                new
                {
                    item_id = itemId,
                    expected_revision = item.Int("revision"),
                    recipe = bad,
                },
                "VALIDATION_ERROR"
            );
            bad = template.DeepClone();
            bad["rig"]!["layers"]![0]!["props"] = J.Node(new { fill = "url(https://invalid.example/image)" });
            await Call(
                client,
                "set_character_recipe",
                new
                {
                    item_id = itemId,
                    expected_revision = item.Int("revision"),
                    recipe = bad,
                },
                "VALIDATION_ERROR"
            );
            var incomplete = template.DeepClone();
            incomplete["missing_details"] = J.Node(new[] { "Hidden elbow has not been drawn" });
            item = await Call(
                client,
                "set_character_recipe",
                new
                {
                    item_id = itemId,
                    expected_revision = item.Int("revision"),
                    recipe = incomplete,
                }
            );
            var preparation = await Call(
                client,
                "prepare_library_item",
                new
                {
                    item_id = itemId,
                    expected_revision = item.Int("revision"),
                    idempotency_key = "incomplete",
                }
            );
            await Wait(preparation.Str("job_id"));
            item = studio.Library.Get(itemId);
            Check(!item["preparation"]!.Bool("ready_for_publication"), "missing anatomy blocks publication");
            item = await Call(
                client,
                "set_character_recipe",
                new
                {
                    item_id = itemId,
                    expected_revision = item.Int("revision"),
                    recipe = template,
                }
            );
            preparation = await Call(
                client,
                "prepare_library_item",
                new
                {
                    item_id = itemId,
                    expected_revision = item.Int("revision"),
                    idempotency_key = "complete",
                }
            );
            await Wait(preparation.Str("job_id"));
            item = studio.Library.Get(itemId);
            var package = studio.Library.Package(itemId, item.Int("revision"));
            Check(package.Bool("render_supported"), "prepared cutout rig is renderable");
            Check(
                CharacterRigs
                    .Nodes(package["rig"]!)
                    .Where(n => n.Str("type") == "cutout")
                    .All(n => n["asset"] != null && n["source_role"] == null),
                "source roles resolved to immutable assets"
            );
            Check(J.FileHash(source) == item.Items("materials").First().Str("sha256"), "source pixels preserved");
            await Call(
                client,
                "publish_library_item",
                new
                {
                    item_id = itemId,
                    expected_revision = item.Int("revision"),
                    version = "1.0.0",
                    review_approved = true,
                    review_note = "Cannot approve before test",
                },
                "CHARACTER_TEST_REQUIRED"
            );
            var audio = await studio.Assets.Register(
                Path.Combine(config.Root, "examples/offline-dialogue/problem.wav"),
                "audio",
                null,
                CancellationToken.None
            );
            var reference = J.Node(new { asset_id = audio.Str("asset_id"), version = audio.Str("version") });
            var args = new
            {
                item_id = itemId,
                expected_revision = item.Int("revision"),
                audio_asset = reference,
                transcript = "Опять весь вечер проверял сайты.",
                idempotency_key = "character-trial",
            };
            var test = await Call(client, "start_character_test", args);
            Check(
                (await Call(client, "start_character_test", args)).Str("job_id") == test.Str("job_id"),
                "test enqueue idempotent"
            );
            await client.DisposeAsync();
            var completed = await Wait(test.Str("job_id"));
            Check(
                completed.Items("artifacts").Any(a => a.Str("name") == "video.mp4"),
                "verified spoken MP4 after MCP disconnect"
            );
            var timeline = J.Read(Path.Combine(config.Data, "jobs", test.Str("job_id"), "timeline.json"));
            Check(
                timeline["rigs"]?[itemId] != null && timeline.Int("total_frames") > 0,
                "timeline pins the prepared rig"
            );
            Check(
                timeline.Items("scenes").Count() == 3 && timeline.Items("utterances").Count() == 3,
                "three measured speech scenes"
            );
            Check(
                completed.Items("artifacts").Count(a => a.Str("name").StartsWith("frame-")) >= 10,
                "control frames published for review"
            );
            // Infrastructure test approval only; visual likeness of real submissions still requires operator review.
            var released = studio.Library.Publish(
                itemId,
                item.Int("revision"),
                "1.0.0",
                true,
                "Synthetic fixture infrastructure acceptance; real likeness and listening remain separate operator checks"
            );
            Check(released.Bool("render_supported"), "tested version published");
            var immutable = studio.Library.ResolveCharacter(itemId, "1.0.0");
            item = studio.Library.SetCharacterRecipe(itemId, released.Int("revision"), incomplete);
            Check(
                J.Canonical(immutable) == J.Canonical(studio.Library.ResolveCharacter(itemId, "1.0.0")),
                "editing draft preserves published rig"
            );
            var renderProject = J.Read(Path.Combine(config.Data, "jobs", test.Str("job_id"), "project.json"));
            renderProject["characters"]![0]!["version"] = "1.0.0";
            var saved = studio.Create(renderProject);
            Check(saved["readiness"]!.Bool("render_ready"), "published custom character works in ordinary projects");
            Check(
                studio
                    .Catalog("characters")
                    .AsArray()
                    .Any(c => c != null && c.Str("character_id") == itemId && c.Str("version") == "1.0.0"),
                "catalog retains published character while draft changes"
            );
            var final = studio.Start(
                saved.Str("project_id"),
                saved.Int("revision"),
                "final",
                "character-final",
                false,
                0
            );
            var finished = await Wait(final.Str("job_id"));
            var exportPath = Path.Combine(config.Data, "jobs", final.Str("job_id"), "export");
            var exportedTimeline = J.Read(Path.Combine(exportPath, "timeline.json"));
            Check(
                J.Canonical(exportedTimeline["rigs"]![itemId]!) == J.Canonical(immutable),
                "final export contains immutable published rig"
            );
            var manifest = J.Read(Path.Combine(exportPath, "manifest.json"));
            Check(
                manifest.Items("dependencies").Any(d => d.Str("path").EndsWith(".png"))
                && finished.Items("artifacts").Any(a => a.Str("name") == "project.zip"),
                "final ZIP contains cutout source media"
            );
            item = studio.Library.SetCharacterRecipe(itemId, item.Int("revision"), template);
            var again = studio.Library.StartPreparation(itemId, item.Int("revision"), "revised-package");
            await Wait(again.Str("job_id"));
            item = studio.Library.Get(itemId);
            try
            {
                studio.Library.Publish(
                    itemId,
                    item.Int("revision"),
                    "2.0.0",
                    true,
                    "Old test must not approve a new package"
                );
                throw new Exception("Old test approved new package");
            }
            catch (ToonException ex)
            {
                Check(ex.Code == "CHARACTER_TEST_REQUIRED", "new revision requires a new test");
            }
            var cancelled = studio.StartCharacterTest(
                itemId,
                item.Int("revision"),
                reference,
                args.transcript,
                "cancel-character-test"
            );
            studio.Cancel(cancelled.Str("job_id"));
            var cancelTimer = Stopwatch.StartNew();
            while (
                studio.Job(cancelled.Str("job_id")).Str("state") is "queued" or "running" or "cancelling"
                && cancelTimer.Elapsed < TimeSpan.FromSeconds(30)
            )
            {
                await Task.Delay(100);
            }
            Check(
                studio.Job(cancelled.Str("job_id")).Str("state") == "cancelled",
                "character test cancellation terminates cleanly"
            );
            var report = J.Read(Path.Combine(config.Data, "jobs", test.Str("job_id"), "validation-report.json"));
            Check(report.Int("new_tts_characters_estimate") == 0, "test uses no paid TTS");
            J.Write(
                Path.Combine(config.Data, "character-acceptance.json"),
                J.Node(
                    new
                    {
                        checks,
                        test_job = test.Str("job_id"),
                        final_job = final.Str("job_id"),
                        item_id = itemId,
                        project_id = saved.Str("project_id"),
                        live_art_review = "pending real sketches",
                    }
                )
            );
            Console.WriteLine("REPORT " + Path.Combine(config.Data, "character-acceptance.json"));
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(true);
            }
            await worker.WaitForExitAsync();
            await Task.WhenAll(stderr, stdout);
        }

        return;

        void Check(bool value, string name)
        {
            if (!value)
                throw new Exception(name);
            checks.Add(J.Node(new { name, status = "pass" }));
            Console.WriteLine("PASS character " + name);
        }
    }
}
