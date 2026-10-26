using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Toon.Core;
using Toon.Infrastructure;

public static class EnvironmentAcceptance
{
    public static async Task Run(Config config)
    {
        var studio = new Studio(config);
        var checks = new JsonArray();
        void Check(bool value, string name)
        {
            if (!value)
                throw new Exception(name);
            checks.Add(J.Node(new { name, status = "pass" }));
            Console.WriteLine("PASS environment " + name);
        }
        async Task<McpClient> Connect() =>
            await McpClient.CreateAsync(
                new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = "Environment acceptance",
                        Command = "dotnet",
                        WorkingDirectory = config.Root,
                        Arguments = [Path.Combine(config.Root, "src/Toon.Mcp/bin/Debug/net10.0/Toon.Mcp.dll")],
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
        async Task<JsonNode> Wait(string id, string state = "succeeded")
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromMinutes(8))
            {
                var job = studio.Job(id);
                if (job.Str("state") == state)
                    return job;

                if (job.Str("state") is "failed" or "cancelled")
                    throw new Exception(job.ToJsonString());
                await Task.Delay(250);
            }
            throw new TimeoutException("Environment worker timed out");
        }
        foreach (var id in Validation.Backgrounds)
        {
            var environment = studio.Library.ResolveEnvironment(id, "1.0.0");
            Check(
                environment.Items("layers").Count() >= 2 && environment["slots"]!.AsObject().Count == 3,
                "builtin environment migrated: " + id
            );
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
            await client.ReadResourceAsync("toon://environments/schema");
            await client.ReadResourceAsync("toon://environments/recipe-schema");
            var template = await Call(client, "get_environment_recipe_template", new { });
            Validation.Schema(template, Path.Combine(config.Root, "contracts/environment-recipe.schema.json"));
            Check(true, "recipe and schemas available through MCP");
            var submission = SubmissionTemplates.Get(config, "environment")["submission"]!.DeepClone();
            submission["display_name"] = "Тестовая мастерская";
            submission["rights_confirmed"] = true;
            var item = await Call(client, "create_library_item", new { submission });
            var id = item.Str("item_id");
            var source = Path.Combine(config.Root, "examples/environment-workshop/wide.png");
            item = await Call(
                client,
                "import_library_material",
                new
                {
                    item_id = id,
                    expected_revision = item.Int("revision"),
                    role = "wide",
                    source_path = source,
                    note = "Original synthetic workshop fixture; not an actual submitted sketch",
                }
            );
            var bad = template.DeepClone();
            bad["environment"]!["layers"]![0]!["nodes"]![0]!["props"] = J.Node(new { onload = "invalid" });
            await Call(
                client,
                "set_environment_recipe",
                new
                {
                    item_id = id,
                    expected_revision = item.Int("revision"),
                    recipe = bad,
                },
                "VALIDATION_ERROR"
            );
            bad = template.DeepClone();
            bad["environment"]!["slots"]!["left"]!["x"] = 1000;
            await Call(
                client,
                "set_environment_recipe",
                new
                {
                    item_id = id,
                    expected_revision = item.Int("revision"),
                    recipe = bad,
                },
                "VALIDATION_ERROR"
            );
            bad = template.DeepClone();
            bad["environment"]!["layers"]![0]!["nodes"]![0]!["props"] = J.Node(
                new { fill = "url(https://invalid.example/image)" }
            );
            await Call(
                client,
                "set_environment_recipe",
                new
                {
                    item_id = id,
                    expected_revision = item.Int("revision"),
                    recipe = bad,
                },
                "VALIDATION_ERROR"
            );
            bad = template.DeepClone();
            bad["environment"]!["layers"]![0]!["nodes"]![0]!["polygon"]![1]![0] = 3000;
            item = await Call(
                client,
                "set_environment_recipe",
                new
                {
                    item_id = id,
                    expected_revision = item.Int("revision"),
                    recipe = bad,
                }
            );
            var preparation = studio.Library.StartPreparation(id, item.Int("revision"), "bad-polygon");
            var failed = await Wait(preparation.Str("job_id"), "failed");
            Check(failed["error"]!.Str("code") == "VALIDATION_ERROR", "measured source bounds enforced by worker");
            item = studio.Library.Get(id);
            Check(item.Str("state") == "draft", "failed preparation recovers editable draft");
            var incomplete = template.DeepClone();
            incomplete["missing_details"] = J.Node(new[] { "Hidden wall behind foreground is unknown" });
            item = studio.Library.SetEnvironmentRecipe(id, item.Int("revision"), incomplete);
            preparation = studio.Library.StartPreparation(id, item.Int("revision"), "incomplete");
            await Wait(preparation.Str("job_id"));
            item = studio.Library.Get(id);
            Check(!item["preparation"]!.Bool("ready_for_publication"), "missing scenery blocks publication");
            item = await Call(
                client,
                "set_environment_recipe",
                new
                {
                    item_id = id,
                    expected_revision = item.Int("revision"),
                    recipe = template,
                }
            );
            var prepArgs = new
            {
                item_id = id,
                expected_revision = item.Int("revision"),
                idempotency_key = "complete",
            };
            preparation = await Call(client, "prepare_library_item", prepArgs);
            Check(
                (await Call(client, "prepare_library_item", prepArgs)).Str("job_id") == preparation.Str("job_id"),
                "preparation idempotent"
            );
            await Wait(preparation.Str("job_id"));
            item = studio.Library.Get(id);
            var package = studio.Library.Package(id, item.Int("revision"));
            Check(package.Bool("render_supported"), "prepared environment renderable");
            Check(
                Environments.Nodes(package["environment"]!).All(n => n["asset"] != null && n["source_role"] == null),
                "source roles resolved to immutable layers"
            );
            Check(
                J.FileHash(source) == item.Items("materials").Single().Str("sha256"),
                "original source pixels preserved"
            );
            await Call(
                client,
                "publish_library_item",
                new
                {
                    item_id = id,
                    expected_revision = item.Int("revision"),
                    version = "1.0.0",
                    review_approved = true,
                    review_note = "No test yet",
                },
                "ENVIRONMENT_TEST_REQUIRED"
            );
            var audio = await studio.Assets.Register(
                Path.Combine(config.Root, "examples/offline-dialogue/problem.wav"),
                "audio",
                null,
                CancellationToken.None
            );
            var reference = J.Node(new { asset_id = audio.Str("asset_id"), version = audio.Str("version") });
            var trialArgs = new
            {
                item_id = id,
                expected_revision = item.Int("revision"),
                audio_asset = reference,
                transcript = "Опять весь вечер проверял сайты.",
                idempotency_key = "environment-test",
            };
            var test = await Call(client, "start_environment_test", trialArgs);
            Check(
                (await Call(client, "start_environment_test", trialArgs)).Str("job_id") == test.Str("job_id"),
                "test enqueue idempotent"
            );
            await client.DisposeAsync();
            var completed = await Wait(test.Str("job_id"));
            Check(
                completed.Items("artifacts").Any(a => a.Str("name") == "video.mp4"),
                "verified MP4 after MCP disconnect"
            );
            var testDirectory = Path.Combine(config.Data, "jobs", test.Str("job_id"));
            var timeline = J.Read(Path.Combine(testDirectory, "timeline.json"));
            Check(
                timeline.Items("scenes").All(s => s.Items("cast").Count() == 2),
                "two control characters in each shot"
            );
            Check(
                timeline.Items("scenes").Count(s => s["camera"]!.Str("shot") == "close_up") == 2,
                "both close-ups rendered"
            );
            Check(
                completed.Items("artifacts").Any(a => a.Str("name") == "environment-review.json"),
                "review metadata and control frames published"
            );
            var released = studio.Library.Publish(
                id,
                item.Int("revision"),
                "1.0.0",
                true,
                "Synthetic fixture infrastructure test approval; real scenery art review remains separate"
            );
            Check(released.Bool("render_supported"), "tested environment published");
            var immutable = studio.Library.ResolveEnvironment(id, "1.0.0");
            item = studio.Library.SetEnvironmentRecipe(id, released.Int("revision"), incomplete);
            Check(
                J.Canonical(immutable) == J.Canonical(studio.Library.ResolveEnvironment(id, "1.0.0")),
                "draft edits preserve published scene"
            );
            Check(
                studio.Catalog("backgrounds").AsArray().Any(e => e != null && e.Str("asset_id") == id),
                "published environment discoverable in catalog"
            );
            var project = J.Read(Path.Combine(testDirectory, "project.json"));
            foreach (var scene in project.Items("scenes"))
            {
                scene["background"]!["version"] = "1.0.0";
            }
            var saved = studio.Create(project);
            Check(saved["readiness"]!.Bool("render_ready"), "ordinary project resolves published environment");
            var final = studio.Start(
                saved.Str("project_id"),
                saved.Int("revision"),
                "final",
                "environment-final",
                false,
                0
            );
            await Wait(final.Str("job_id"));
            var export = Path.Combine(config.Data, "jobs", final.Str("job_id"), "export");
            Check(
                J.Canonical(J.Read(Path.Combine(export, "timeline.json"))["environments"]![id + "@1.0.0"]!)
                    == J.Canonical(immutable),
                "final export pins exact environment"
            );
            Check(
                J.Read(Path.Combine(export, "manifest.json"))
                    .Items("dependencies")
                    .Any(d => d.Str("path").EndsWith(".png")),
                "ZIP includes source images"
            );
            foreach (var frame in Directory.GetFiles(testDirectory, "frame-*.png"))
            {
                Check(
                    J.FileHash(frame)
                        == J.FileHash(Path.Combine(config.Data, "jobs", final.Str("job_id"), Path.GetFileName(frame))),
                    "deterministic " + Path.GetFileName(frame)
                );
            }
            item = studio.Library.SetEnvironmentRecipe(id, item.Int("revision"), template);
            preparation = studio.Library.StartPreparation(id, item.Int("revision"), "changed-package");
            await Wait(preparation.Str("job_id"));
            item = studio.Library.Get(id);
            try
            {
                studio.Library.Publish(id, item.Int("revision"), "2.0.0", true, "Must reject old test");
                throw new Exception("Old test approved changed environment");
            }
            catch (ToonException ex)
            {
                Check(ex.Code == "ENVIRONMENT_TEST_REQUIRED", "changed package requires new test");
            }
            var cancelled = studio.StartEnvironmentTest(
                id,
                item.Int("revision"),
                reference,
                trialArgs.transcript,
                "cancel-environment-test"
            );
            studio.Cancel(cancelled.Str("job_id"));
            await Wait(cancelled.Str("job_id"), "cancelled");
            Check(
                !studio.Job(cancelled.Str("job_id")).Items("artifacts").Any(),
                "cancelled environment test publishes no artifacts"
            );
            var validation = J.Read(Path.Combine(testDirectory, "validation-report.json"));
            Check(validation.Int("new_tts_characters_estimate") == 0, "no paid TTS");
            J.Write(
                Path.Combine(config.Data, "environment-acceptance.json"),
                J.Node(
                    new
                    {
                        checks,
                        test_job = test.Str("job_id"),
                        final_job = final.Str("job_id"),
                        item_id = id,
                        project_id = saved.Str("project_id"),
                        real_art_review = "pending supplied sketches",
                    }
                )
            );
            Console.WriteLine("REPORT " + Path.Combine(config.Data, "environment-acceptance.json"));
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(true);
            }
            await worker.WaitForExitAsync();
            await File.WriteAllTextAsync(
                Path.Combine(config.Data, "environment-worker.log"),
                await stderr + await stdout
            );
        }
    }
}
