using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Toon.Core;
using Toon.Infrastructure;

public static class LibraryAcceptance
{
    public static async Task Run(Config initial)
    {
        var root = initial.Root;
        var fixtures = Path.Combine(initial.Data, "library-fixtures");
        Directory.CreateDirectory(fixtures);
        Environment.SetEnvironmentVariable(
            "TOON_IMPORT_ROOTS",
            fixtures + Path.PathSeparator + Path.Combine(root, "examples")
        );
        // Independently exercise migration from a v1 Store with a real saved draft, before creating a Studio.
        Environment.SetEnvironmentVariable("TOON_DATA_ROOT", Path.Combine(initial.Data, "library"));
        var config = new Config();
        var oldStore = new Store(config);
        var oldProject = oldStore.SaveProject(J.Read(Path.Combine(root, "examples/offline-dialogue/project.json")));
        var studio = new Studio(config);
        var library = studio.Library;
        var checks = new JsonArray();

        void Check(bool value, string name)
        {
            if (!value)
                throw new Exception(name);

            checks.Add(J.Node(new { name, status = "pass" }));
            Console.WriteLine("PASS library " + name);
        }

        void Expect(string code, Action work)
        {
            try
            {
                work();
                throw new Exception("Expected " + code);
            }
            catch (ToonException ex)
            {
                Check(ex.Code == code, code);
            }
        }

        async Task<McpClient> Connect() =>
            await McpClient.CreateAsync(
                new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = "Library acceptance",
                        Command = "dotnet",
                        Arguments = [Path.Combine(root, "src/Toon.Mcp/bin/Debug/net10.0/Toon.Mcp.dll")],
                        WorkingDirectory = root,
                    }
                )
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

            if (result.IsError == true || !body.Bool("ok"))
                throw new Exception("MCP " + name + ": " + body.ToJsonString());

            return body["data"]!;
        }

        async Task<JsonNode> Wait(string id)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(60))
            {
                var job = studio.Job(id);
                if (job.Str("state") == "succeeded")
                    return job;

                if (job.Str("state") is "failed" or "cancelled")
                    throw new Exception(job.ToJsonString());

                await Task.Delay(100);
            }

            throw new TimeoutException("Library worker timed out");
        }

        var image = Path.Combine(root, "examples/offline-dialogue/screen.png");
        var sample = Path.Combine(fixtures, "tone.wav");
        await Processes.Run(
            config.Ffmpeg,
            ["-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "pcm_s16le", sample],
            root,
            CancellationToken.None
        );
        var publishedId = "";
        var publishedRevision = 0;
        Process? worker = null;
        Task<string>? stderr = null;
        Task<string>? stdout = null;
        try
        {
            Check(
                studio.Store.Project(oldProject.Str("project_id"), 1)["project"]!.ToJsonString()
                    == J.Read(Path.Combine(root, "examples/offline-dialogue/project.json")).ToJsonString(),
                "v1 project preserved by migration"
            );
            Check(
                library.List().AsArray().Count == 5 && new Studio(config).Library.List().AsArray().Count == 5,
                "builtin seeding is idempotent"
            );
            using (var db = studio.Store.Open())
            using (var cmd = Store.Command(db, null, "SELECT COUNT(*) FROM migrations WHERE version=2"))
                Check(Convert.ToInt32(cmd.ExecuteScalar()) == 1, "migration v2 recorded once");
            JsonNode character;
            string jobId;
            await using (var client = await Connect())
            {
                foreach (var kind in SubmissionTemplates.Kinds)
                {
                    var template = await Call(client, "get_submission_template", new { kind });
                    Validation.Schema(
                        template["submission"]!,
                        Path.Combine(root, "contracts/library-submission.schema.json")
                    );
                    Check(
                        template.Items("materials").Any(m => m.Bool("required")) && !template.Bool("cloud_requests"),
                        "template " + kind
                    );
                    await client.ReadResourceAsync($"toon://library/templates/{kind}/1.0.0");
                }

                await client.ReadResourceAsync("toon://library/submission-schema");
                var submission = SubmissionTemplates.Get(config, "character")["submission"]!.DeepClone();
                submission["rights_confirmed"] = true;
                character = await Call(client, "create_library_item", new { submission });
                var id = character.Str("item_id");
                character = await Call(
                    client,
                    "import_library_material",
                    new
                    {
                        item_id = id,
                        expected_revision = 1,
                        role = "front",
                        source_path = image,
                        note = "Explicit synthetic fixture, not a real character sketch",
                    }
                );
                character = await Call(
                    client,
                    "import_library_material",
                    new
                    {
                        item_id = id,
                        expected_revision = 2,
                        role = "three_quarter",
                        source_path = image,
                        note = "Explicit synthetic fixture",
                    }
                );
                await Call(
                    client,
                    "update_library_item",
                    new
                    {
                        item_id = id,
                        expected_revision = 1,
                        submission,
                    },
                    "REVISION_CONFLICT"
                );
                var job = await Call(
                    client,
                    "prepare_library_item",
                    new
                    {
                        item_id = id,
                        expected_revision = 3,
                        idempotency_key = "character-prepare",
                    }
                );
                jobId = job.Str("job_id");
                await Call(
                    client,
                    "update_library_item",
                    new
                    {
                        item_id = id,
                        expected_revision = 4,
                        submission,
                    },
                    "LIBRARY_STATE_CONFLICT"
                );
                var repeat = await Call(
                    client,
                    "prepare_library_item",
                    new
                    {
                        item_id = id,
                        expected_revision = 3,
                        idempotency_key = "character-prepare",
                    }
                );
                Check(
                    jobId == repeat.Str("job_id") && !job.Bool("worker_alive"),
                    "durable idempotent preparation with worker offline"
                );
                await Call(
                    client,
                    "prepare_library_item",
                    new
                    {
                        item_id = id,
                        expected_revision = 4,
                        idempotency_key = "character-prepare",
                    },
                    "IDEMPOTENCY_CONFLICT"
                );
            }
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(Path.Combine(root, "src/Toon.Worker/bin/Debug/net10.0/Toon.Worker.dll"));
            worker = Process.Start(start)!;
            stderr = worker.StandardError.ReadToEndAsync();
            stdout = worker.StandardOutput.ReadToEndAsync();
            var finished = await Wait(jobId);
            Check(
                finished["result"]!.Bool("ready_for_publication") && !finished.Items("artifacts").Any(),
                "worker completed after MCP disconnect; no video artifacts"
            );
            publishedId = character.Str("item_id");
            character = library.Get(publishedId);
            Check(
                character.Str("state") == "review" && !character.Bool("render_supported"),
                "review is distinct from render readiness"
            );
            var package = library.Package(publishedId, character.Int("revision"));
            Check(
                package.Items("materials").All(m => m.Str("sha256") == J.FileHash(image))
                    && !package.Bool("cloud_requests"),
                "prepared package preserves original hashes"
            );
            Check(
                !File.ReadAllText(character.Str("manifest_path")).Contains(image, StringComparison.Ordinal),
                "source absolute path not retained"
            );
            Expect(
                "REVIEW_REQUIRED",
                () => library.Publish(publishedId, character.Int("revision"), "1.0.0", false, "fixture")
            );
            await using (var client = await Connect())
            {
                await client.ReadResourceAsync(
                    $"toon://library/{publishedId}/revisions/{character.Int("revision")}/package"
                );
                var materialLinks = await Call(
                    client,
                    "get_library_materials",
                    new { item_id = publishedId, revision = character.Int("revision") }
                );
                Check(
                    materialLinks.AsArray().Count == 2
                        && materialLinks
                            .AsArray()
                            .Select(m => m!)
                            .All(m =>
                                File.Exists(m.Str("local_path")) && m.Str("sha256") == J.FileHash(m.Str("local_path"))
                            ),
                    "original material links available for review"
                );
                character = await Call(
                    client,
                    "publish_library_item",
                    new
                    {
                        item_id = publishedId,
                        expected_revision = character.Int("revision"),
                        version = "1.0.0",
                        review_approved = true,
                        review_note = "Offline fixture: only package plumbing reviewed, not artwork.",
                    }
                );
                publishedRevision = character.Int("revision");
                var before = character.Str("content_sha256");
                var edit = character["submission"]!.DeepClone();
                edit["display_name"] = "Вася Пупкин — новая версия";
                character = await Call(
                    client,
                    "update_library_item",
                    new
                    {
                        item_id = publishedId,
                        expected_revision = character.Int("revision"),
                        submission = edit,
                    }
                );
                Check(character.Str("state") == "draft" && character["preparation"] == null, "editing clears review");
                Check(
                    library.Get(publishedId, version: "1.0.0").Str("content_sha256") == before,
                    "published v1 remains immutable"
                );
                var job = await Call(
                    client,
                    "prepare_library_item",
                    new
                    {
                        item_id = publishedId,
                        expected_revision = character.Int("revision"),
                        idempotency_key = "second-version",
                    }
                );
                await Wait(job.Str("job_id"));
                character = library.Get(publishedId);
                await Call(
                    client,
                    "publish_library_item",
                    new
                    {
                        item_id = publishedId,
                        expected_revision = character.Int("revision"),
                        version = "1.0.0",
                        review_approved = true,
                        review_note = "fixture",
                    },
                    "LIBRARY_VERSION_CONFLICT"
                );
                character = await Call(
                    client,
                    "publish_library_item",
                    new
                    {
                        item_id = publishedId,
                        expected_revision = character.Int("revision"),
                        version = "1.1.0",
                        review_approved = true,
                        review_note = "Offline fixture review.",
                    }
                );
                Check(
                    (await Call(client, "list_library_versions", new { item_id = publishedId })).AsArray().Count == 2,
                    "two published versions discoverable"
                );
                await Call(
                    client,
                    "archive_library_item",
                    new { item_id = publishedId, expected_revision = character.Int("revision") }
                );
                Check(
                    library.Get(publishedId).Str("state") == "archived"
                        && library.Get(publishedId, version: "1.0.0").Int("revision") == publishedRevision,
                    "archive retains published versions"
                );
            }
            foreach (var kind in new[] { "environment", "voice" })
            {
                var submission = SubmissionTemplates.Get(config, kind)["submission"]!.DeepClone();
                submission["rights_confirmed"] = true;
                var item = library.Create(submission);
                var role = kind == "voice" ? "sample" : "wide";
                item = await library.Import(
                    item.Str("item_id"),
                    1,
                    role,
                    kind == "voice" ? sample : image,
                    "Explicit offline fixture",
                    CancellationToken.None
                );
                var job = library.StartPreparation(item.Str("item_id"), item.Int("revision"), kind);
                await Wait(job.Str("job_id"));
                item = library.Get(item.Str("item_id"));
                Check(item["preparation"]!.Bool("ready_for_publication"), kind + " package ready");
                if (kind == "environment")
                {
                    var packagePath = item["preparation"]!.Str("package_path");
                    var packageBytes = await File.ReadAllBytesAsync(packagePath);
                    try
                    {
                        await File.WriteAllTextAsync(packagePath, "{}");
                        Expect(
                            "LIBRARY_STORAGE_ERROR",
                            () => library.Publish(item.Str("item_id"), item.Int("revision"), "1.0.0", true, "fixture")
                        );
                    }
                    finally
                    {
                        await File.WriteAllBytesAsync(packagePath, packageBytes);
                    }

                    var materialPath = library.Materials(item.Str("item_id"), item.Int("revision"))[0]!.Str(
                        "local_path"
                    );
                    var materialBytes = await File.ReadAllBytesAsync(materialPath);
                    try
                    {
                        await File.WriteAllTextAsync(materialPath, "corrupted fixture");
                        Expect("ASSET_MISSING", () => library.Materials(item.Str("item_id"), item.Int("revision")));
                        Expect(
                            "ASSET_MISSING",
                            () => library.Publish(item.Str("item_id"), item.Int("revision"), "1.0.0", true, "fixture")
                        );
                    }
                    finally
                    {
                        await File.WriteAllBytesAsync(materialPath, materialBytes);
                    }
                }

                if (kind == "voice")
                {
                    Check(
                        item["preparation"]!.Items("warnings").Any(w => w.Str("code") == "SHORT_VOICE_SAMPLE")
                            && !item["submission"]!["details"]!.Bool("cloning_consent"),
                        "voice stays local; short non-speech fixture is not a cloned voice"
                    );
                }
                item = library.Publish(
                    item.Str("item_id"),
                    item.Int("revision"),
                    "1.0.0",
                    true,
                    "Offline fixture review only."
                );
                var hash = item.Str("content_sha256");
                var itemId = item.Str("item_id");
                item = library.RemoveMaterial(itemId, item.Int("revision"), role);
                Check(
                    !item.Items("materials").Any()
                        && library.Get(itemId, version: "1.0.0").Str("content_sha256") == hash,
                    "material removal preserves old " + kind
                );
            }

            var incomplete = library.Create(SubmissionTemplates.Get(config, "character")["submission"]!);
            var incompleteJob = library.StartPreparation(incomplete.Str("item_id"), 1, "incomplete");
            await Wait(incompleteJob.Str("job_id"));
            incomplete = library.Get(incomplete.Str("item_id"));
            Check(
                incomplete["preparation"]!.Items("errors").Count() == 3
                    && !incomplete["preparation"]!.Bool("ready_for_publication"),
                "missing roles and rights reported precisely"
            );
            Expect(
                "LIBRARY_NOT_READY",
                () => library.Publish(incomplete.Str("item_id"), incomplete.Int("revision"), "1.0.0", true, "fixture")
            );
            var invalid = SubmissionTemplates.Get(config, "character")["submission"]!.DeepClone();
            invalid["api_key"] = "fixture-not-a-key";
            Expect("VALIDATION_ERROR", () => library.Create(invalid));
            Expect(
                "LIBRARY_READ_ONLY",
                () =>
                    library.Update(
                        "builtin_character_buyer",
                        1,
                        SubmissionTemplates.Get(config, "character")["submission"]!
                    )
            );
            Expect("LIBRARY_NOT_FOUND", () => library.Get("../../outside"));
            var sameName = SubmissionTemplates.Get(config, "character")["submission"]!;
            var a = library.Create(sameName);
            var b = library.Create(sameName);
            Check(a.Str("item_id") != b.Str("item_id"), "duplicate display names have distinct IDs");
            var outcomes = await Task.WhenAll(
                Enumerable
                    .Range(0, 2)
                    .Select(_ =>
                        Task.Run(() =>
                        {
                            try
                            {
                                library.Update(a.Str("item_id"), 1, sameName);

                                return "saved";
                            }
                            catch (ToonException ex)
                            {
                                return ex.Code;
                            }
                        })
                    )
            );
            Check(
                outcomes.Count(o => o == "saved") == 1 && outcomes.Count(o => o == "REVISION_CONFLICT") == 1,
                "concurrent updates have one winner"
            );
            await using (var client = await Connect())
            {
                await Call(
                    client,
                    "import_library_material",
                    new
                    {
                        item_id = b.Str("item_id"),
                        expected_revision = 1,
                        role = "front",
                        source_path = Path.Combine(root, "contracts/library-submission.schema.json"),
                    },
                    "VALIDATION_ERROR"
                );
                await Call(
                    client,
                    "import_library_material",
                    new
                    {
                        item_id = b.Str("item_id"),
                        expected_revision = 1,
                        role = "sample",
                        source_path = sample,
                    },
                    "VALIDATION_ERROR"
                );
                var fake = Path.Combine(fixtures, "fake.png");
                await File.WriteAllTextAsync(fake, "<script>fixture</script>");
                await Call(
                    client,
                    "import_library_material",
                    new
                    {
                        item_id = b.Str("item_id"),
                        expected_revision = 1,
                        role = "front",
                        source_path = fake,
                    },
                    "VALIDATION_ERROR"
                );
            }
            // Stop only our isolated worker. Queue/cancellation/lease cases below have no timing races.
            worker.Kill(true);
            await worker.WaitForExitAsync();
            worker.Dispose();
            worker = null;
            var pending = library.StartPreparation(b.Str("item_id"), 1, "cancel-queued");
            studio.Cancel(pending.Str("job_id"));
            Check(
                library.Get(b.Str("item_id")).Str("state") == "draft"
                    && studio.Job(pending.Str("job_id")).Str("state") == "cancelled",
                "queued cancellation recovers editable draft"
            );
            b = library.Get(b.Str("item_id"));
            pending = library.StartPreparation(b.Str("item_id"), b.Int("revision"), "cancel-running");
            var claimed = studio.Store.Claim()!;
            Check(claimed.Str("job_id") == pending.Str("job_id"), "library job claimed");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await new Worker(studio).Execute(claimed, cancelled.Token);
            }
            Check(
                library.Get(b.Str("item_id")).Str("state") == "draft"
                    && studio.Job(pending.Str("job_id")).Str("state") == "cancelled",
                "cancelled execution cannot publish package"
            );
            b = library.Get(b.Str("item_id"));
            pending = library.StartPreparation(b.Str("item_id"), b.Int("revision"), "expired-lease");
            _ = studio.Store.Claim();
            studio.Store.ChangeJob(
                pending.Str("job_id"),
                j => j["lease_until"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")
            );
            Check(
                library.Get(b.Str("item_id"))["last_error"]!.Str("code") == "WORKER_INTERRUPTED",
                "expired lease fixture recovers editable draft"
            );
            b = library.Get(b.Str("item_id"));
            pending = library.StartPreparation(b.Str("item_id"), b.Int("revision"), "retry-after-interruption");
            await new Worker(studio).Execute(studio.Store.Claim()!, CancellationToken.None);
            Check(
                studio.Job(pending.Str("job_id")).Str("state") == "succeeded",
                "explicit retry after interruption succeeds"
            );
            var saved = library.Get(publishedId, version: "1.0.0");
            var original = await File.ReadAllBytesAsync(saved.Str("manifest_path"));
            try
            {
                await File.WriteAllTextAsync(saved.Str("manifest_path"), "{}");
                Expect("LIBRARY_STORAGE_ERROR", () => library.Get(publishedId, version: "1.0.0"));
            }
            finally
            {
                await File.WriteAllBytesAsync(saved.Str("manifest_path"), original);
            }

            Check(
                new Studio(config).Library.Get(publishedId, version: "1.0.0").Int("revision") == publishedRevision,
                "reopen retains releases"
            );
        }
        finally
        {
            if (worker != null)
            {
                if (!worker.HasExited)
                {
                    worker.Kill(true);
                }
                await worker.WaitForExitAsync();
                worker.Dispose();
            }

            if (stderr != null)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(config.Data, "library-worker.log"),
                    await stderr + (stdout == null ? "" : await stdout)
                );
            }
            J.Write(
                Path.Combine(config.Data, "library-acceptance.json"),
                J.Node(
                    new
                    {
                        checks,
                        published_id = publishedId,
                        fixture_scope = "synthetic screen and tone; no actual artwork/voice-cloning acceptance",
                    }
                )
            );
            Console.WriteLine("REPORT " + Path.Combine(config.Data, "library-acceptance.json"));
        }
    }
}
