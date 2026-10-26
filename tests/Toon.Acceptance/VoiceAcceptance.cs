using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using Toon.Core;
using Toon.Infrastructure;

namespace Toon.Acceptance;

public static class VoiceAcceptance
{
    public static async Task Run(Config originalConfig)
    {
        var input = Path.Combine(originalConfig.Data, "input");
        Directory.CreateDirectory(input);
        Environment.SetEnvironmentVariable(
            "TOON_IMPORT_ROOTS",
            Path.Combine(originalConfig.Root, "examples") + Path.PathSeparator + input
        );
        Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", null);
        Environment.SetEnvironmentVariable("TOON_ALLOW_VOICE_CLONING", "1");
        Environment.SetEnvironmentVariable("TOON_MAX_VOICE_CLONES", "10");
        var config = new Config();
        var checks = new JsonArray();
        var samplePath = Path.Combine(input, "speech-fixture.wav");
        var speechPath = Path.Combine(config.Root, "examples/offline-dialogue/problem.wav");
        await Processes.Run(
            config.Ffmpeg,
            ["-v", "error", "-y", "-stream_loop", "3", "-i", speechPath, samplePath],
            config.Root,
            CancellationToken.None
        );
        var mp3 = Path.Combine(input, "speech-fixture.mp3");
        await Processes.Run(
            config.Ffmpeg,
            ["-v", "error", "-y", "-i", speechPath, "-c:a", "libmp3lame", mp3],
            config.Root,
            CancellationToken.None
        );
        using var transport = new VoiceTransport(
            await File.ReadAllBytesAsync(mp3),
            await File.ReadAllBytesAsync(samplePath)
        );
        using var http = new HttpClient(transport);
        var studio = new Studio(config, http);
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new Exception(name);
            checks.Add(J.Node(new { name, status = "pass" }));
            Console.WriteLine("PASS voice " + name);
        }
        void Reject(Action action, string code)
        {
            try
            {
                action();
                throw new Exception("Expected " + code);
            }
            catch (ToonException ex)
            {
                Check(ex.Code == code, code);
            }
        }
        async Task<JsonNode> Execute(JsonNode submitted, string state = "succeeded", string? error = null)
        {
            var claimed = studio.Store.Claim()!;
            Check(
                claimed != null && claimed.Str("job_id") == submitted.Str("job_id"),
                "durable job claimed: " + submitted["job_kind"]
            );
            await new Worker(studio).Execute(claimed!, CancellationToken.None);
            var job = studio.Job(submitted.Str("job_id"));
            Check(
                job.Str("state") == state && (error == null || job["error"]!.Str("code") == error),
                "worker outcome " + (error ?? state)
            );
            return job;
        }
        async Task<JsonNode> NewSource(string name, bool consent = true, string? description = null)
        {
            var submission = SubmissionTemplates.Get(config, "voice")["submission"]!.DeepClone();
            submission["display_name"] = name;
            if (description != null)
            {
                submission["description"] = description;
            }
            submission["rights_confirmed"] = true;
            submission["details"]!["cloning_consent"] = consent;
            submission["details"]!["cloud_upload_consent"] = consent;
            var item = studio.Library.Create(submission);
            item = await studio.Library.Import(
                item.Str("item_id"),
                item.Int("revision"),
                "sample",
                samplePath,
                "Synthetic SAPI fixture; not an authorized real actor sample",
                CancellationToken.None
            );
            await Execute(
                studio.Library.StartPreparation(
                    item.Str("item_id"),
                    item.Int("revision"),
                    "prepare-" + item.Str("item_id")
                )
            );
            return studio.Library.Get(item.Str("item_id"));
        }
        JsonNode Clone(JsonNode item, string key) =>
            studio.Voices.StartClone(
                item.Str("item_id"),
                item.Int("revision"),
                true,
                1,
                true,
                "Offline transport fixture only; not a human listening approval",
                key
            );
        var item = await NewSource("Offline voice fixture");
        var package = studio.Library.Package(item.Str("item_id"), item.Int("revision"));
        Check(
            package["voice_analysis"]!.Bool("technical_ready")
                && package["voice_analysis"]!.Str("listening_review") == "pending_operator",
            "signal analysis keeps human review pending"
        );
        Check(package["voice_analysis"]!.Str("source_sha256") == J.FileHash(samplePath), "original sample preserved");
        foreach (var (name, expression) in new[] { ("silence", "0"), ("clipped", "1") })
        {
            var path = Path.Combine(input, name + ".wav");
            await Processes.Run(
                config.Ffmpeg,
                [
                    "-v",
                    "error",
                    "-y",
                    "-f",
                    "lavfi",
                    "-i",
                    "aevalsrc=" + expression + ":s=48000:d=12",
                    "-c:a",
                    "pcm_s16le",
                    path,
                ],
                config.Root,
                CancellationToken.None
            );
            var asset = await studio.Assets.Register(path, "audio", null, CancellationToken.None);
            var result = await VoiceSamples.Analyze(config, studio.Assets, asset, CancellationToken.None);
            Check(!result.Bool("technical_ready"), name + " rejected");
        }
        var shortAsset = await studio.Assets.Register(speechPath, "audio", null, CancellationToken.None);
        Check(
            !(await VoiceSamples.Analyze(config, studio.Assets, shortAsset, CancellationToken.None)).Bool(
                "technical_ready"
            ),
            "too short sample rejected"
        );
        var noConsent = await NewSource("No consent fixture", false);
        Reject(() => Clone(noConsent, "no-consent"), "VOICE_CONSENT_REQUIRED");
        Environment.SetEnvironmentVariable("TOON_ALLOW_VOICE_CLONING", "0");
        Reject(() => Clone(item, "disabled"), "VOICE_CLONING_DISABLED");
        Environment.SetEnvironmentVariable("TOON_ALLOW_VOICE_CLONING", "1");
        Reject(
            () =>
                studio.Voices.StartClone(
                    item.Str("item_id"),
                    item.Int("revision"),
                    true,
                    1,
                    false,
                    "Not listened",
                    "unreviewed"
                ),
            "REVIEW_REQUIRED"
        );

        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = "Voice acceptance",
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
        async Task<JsonNode> Call(string tool, object args)
        {
            var response = await client.CallToolAsync(
                tool,
                J.Node(args).AsObject().ToDictionary(p => p.Key, p => (object?)p.Value)
            );
            var body = JsonNode.Parse(response.StructuredContent!.Value.GetRawText())!;
            if (response.IsError == true)
                throw new Exception(body.ToJsonString());
            return body["data"]!;
        }
        var configureTool = (await client.ListToolsAsync()).Single(tool => tool.Name == "configure_voice_profile");
        var configurationSchema = Json.Schema.JsonSchema.FromText(configureTool.ProtocolTool.InputSchema.GetRawText());
        var configurationArguments = J.Node(
            new
            {
                profile_id = "schema_fixture",
                expected_revision = 1,
                voice_settings = new VoiceSettings
                {
                    Stability = .3,
                    SimilarityBoost = .85,
                    Style = .6,
                    UseSpeakerBoost = true,
                    Speed = .7,
                },
                note = "Offline schema fixture",
            }
        );
        Check(
            configurationSchema.Evaluate(configurationArguments).IsValid,
            "MCP configuration schema accepts settings"
        );
        configurationArguments["voice_settings"]!.AsObject().Remove("speed");
        Check(
            !configurationSchema.Evaluate(configurationArguments).IsValid,
            "MCP configuration schema requires all delivery fields"
        );
        var cloneArgs = new
        {
            item_id = item.Str("item_id"),
            expected_revision = item.Int("revision"),
            allow_cloud_upload = true,
            max_clone_requests = 1,
            sample_reviewed = true,
            review_note = "Explicit offline fixture review; no real cloud requests",
            idempotency_key = "mcp-clone",
        };
        var clone = await Call("start_voice_clone", cloneArgs);
        Check(
            (await Call("start_voice_clone", cloneArgs)).Str("job_id") == clone.Str("job_id"),
            "MCP clone idempotent"
        );
        await client.DisposeAsync();
        var missingKey = await Execute(clone, "failed", "VOICE_NOT_CONFIGURED");
        Check(transport.Clones == 0, "missing key sends no request");
        Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", "offline-fixture-not-a-secret");
        transport.RequireVerification = true;
        clone = await Execute(Clone(item, "verified-fixture"));
        var cloneOperation = studio.Store.Job(clone.Str("job_id"))["snapshot"]!.Str("operation_key");
        Check(
            transport.LastDescription
                == item["submission"]!.Str("description") + "\n\n[toon_request:" + cloneOperation + "]",
            "cloud description preserves source text and complete deterministic operation marker"
        );
        var profile = clone["result"]!["profile"]!;
        Check(profile.Str("state") == "verification_required", "provider verification is enforced");
        Reject(
            () =>
                studio.Voices.StartPreview(
                    profile.Str("profile_id"),
                    profile.Int("revision"),
                    "Проба",
                    true,
                    100,
                    "blocked-preview"
                ),
            "VOICE_VERIFICATION_REQUIRED"
        );
        transport.RequireVerification = false;
        profile = await studio.Voices.Refresh(
            profile.Str("profile_id"),
            profile.Int("revision"),
            CancellationToken.None
        );
        Check(profile.Str("state") == "pending_review", "provider verification is separate from listening review");
        Reject(
            () => studio.Voices.Review(profile.Str("profile_id"), profile.Int("revision"), true, "No preview yet"),
            "VOICE_PREVIEW_REQUIRED"
        );
        const string text = "Опять весь вечер проверял сайты.";
        Reject(
            () =>
                studio.Voices.StartPreview(
                    profile.Str("profile_id"),
                    profile.Int("revision"),
                    text,
                    false,
                    0,
                    "no-budget"
                ),
            "TTS_LIMIT_EXCEEDED"
        );
        var preview = await Execute(
            studio.Voices.StartPreview(profile.Str("profile_id"), profile.Int("revision"), text, true, 120, "preview")
        );
        Check(studio.Artifacts(preview.Str("job_id")).AsArray().Count == 2, "verified WAV and metadata published");
        Check(transport.Syntheses == 1, "one controlled offline synthesis");
        await Execute(
            studio.Voices.StartPreview(
                profile.Str("profile_id"),
                profile.Int("revision"),
                text,
                false,
                0,
                "cached-preview"
            )
        );
        Check(transport.Syntheses == 1, "cached preview works with zero new TTS budget");
        profile = studio.Voices.Review(
            profile.Str("profile_id"),
            profile.Int("revision"),
            true,
            "Infrastructure fixture approval only; real cloned voice listening remains pending"
        );
        var approved = profile.DeepClone();
        var binding = studio.Voices.Bind("buyer", "1.0.0", 0, profile.Str("profile_id"), profile.Int("revision"));
        studio.Voices.Bind("advisor", "1.0.0", 0, profile.Str("profile_id"), profile.Int("revision"));
        Check(binding.Int("revision") == 1, "approved voice binds to existing character");
        Reject(
            () => studio.Voices.Bind("buyer", "1.0.0", 0, profile.Str("profile_id"), profile.Int("revision")),
            "REVISION_CONFLICT"
        );
        var project = J.Read(Path.Combine(config.Root, "examples/offline-dialogue/project.json"));
        project["title"] = "OFFLINE voice binding fixture — not an ElevenLabs clone";
        foreach (var line in project.Items("scenes").SelectMany(s => s.Items("utterances")))
        {
            line["text"] = text;
            line["audio"] = J.Node(
                new
                {
                    mode = "tts",
                    voice_alias = "character",
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
        var savedProject = studio.GetProject(saved.Str("project_id"), saved.Int("revision"));
        Check(
            savedProject["project"]!
                .Items("characters")
                .All(c => c["voice_profile"]!.Int("revision") == approved.Int("revision")),
            "project pins immutable profile revision"
        );
        Check(saved["readiness"]!.Int("estimated_new_tts_characters") == 0, "render reuses approved preview cache");
        var final = studio.Start(saved.Str("project_id"), saved.Int("revision"), "final", "voice-final", false, 0);
        // Actual independent worker has no API key and can only use the already verified cache.
        Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", null);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = config.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(Path.Combine(config.Root, "src/Toon.Worker/bin/Debug/net10.0/Toon.Worker.dll"));
        using (var worker = Process.Start(start)!)
        {
            var stderr = worker.StandardError.ReadToEndAsync();
            var stdout = worker.StandardOutput.ReadToEndAsync();
            try
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < TimeSpan.FromMinutes(8))
                {
                    var state = studio.Job(final.Str("job_id")).Str("state");
                    if (state == "succeeded")
                    {
                        break;
                    }

                    if (state is "failed" or "cancelled")
                        throw new Exception(studio.Job(final.Str("job_id")).ToJsonString());
                    await Task.Delay(300);
                }
                Check(
                    studio.Job(final.Str("job_id")).Str("state") == "succeeded",
                    "independent worker renders pinned voice after MCP disconnect without API key"
                );
            }
            finally
            {
                if (!worker.HasExited)
                {
                    worker.Kill(true);
                }
                await worker.WaitForExitAsync();
                await File.WriteAllTextAsync(
                    Path.Combine(config.Data, "voice-worker.log"),
                    await stdout + await stderr
                );
            }
        }
        Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", "offline-fixture-not-a-secret");
        Check(
            new Studio(config, http).Voices.Binding("buyer", "1.0.0")!.Int("revision") == 1,
            "binding survives reopen"
        );
        profile = studio.Voices.Review(profile.Str("profile_id"), profile.Int("revision"), false, "Test revocation");
        Reject(
            () =>
                studio.Voices.Resolve(
                    J.Node(new { profile_id = approved.Str("profile_id"), revision = approved.Int("revision") })
                ),
            "VOICE_NOT_READY"
        );
        Check(
            studio.Voices.Profile(approved.Str("profile_id"), approved.Int("revision")).Str("state") == "approved",
            "old profile revision remains immutable"
        );
        var disabledProfile = studio.Voices.Profile(approved.Str("profile_id"));
        Reject(
            () =>
                studio.Voices.Review(
                    disabledProfile.Str("profile_id"),
                    disabledProfile.Int("revision"),
                    true,
                    "Missing fresh review"
                ),
            "VOICE_PREVIEW_REQUIRED"
        );
        await Execute(
            studio.Voices.StartPreview(
                disabledProfile.Str("profile_id"),
                disabledProfile.Int("revision"),
                text,
                false,
                0,
                "review-rejected"
            )
        );
        var restoredProfile = studio.Voices.Review(
            disabledProfile.Str("profile_id"),
            disabledProfile.Int("revision"),
            true,
            "Offline fixture reviewed again"
        );
        Check(
            restoredProfile.Str("state") == "approved" && transport.Syntheses == 1,
            "rejected voice can be reviewed again using verified cache"
        );

        var uncertain = await NewSource("Uncertain clone fixture");
        transport.FailAfterDispatch = true;
        var failed = await Execute(Clone(uncertain, "uncertain"), "failed", "PROVIDER_OUTCOME_UNKNOWN");
        var calls = transport.Clones;
        await Execute(Clone(uncertain, "uncertain-again"), "failed", "PROVIDER_OUTCOME_UNKNOWN");
        Check(transport.Clones == calls, "uncertain clone is never automatically repeated");
        transport.FailAfterDispatch = false;
        transport.DescriptionOverride = transport.LastDescription + " trailing text";
        try
        {
            await studio.Voices.Reconcile(
                failed.Str("job_id"),
                transport.LastVoiceId,
                "A partial or nonterminal marker must not match",
                CancellationToken.None
            );
            throw new Exception("Expected description marker mismatch");
        }
        catch (ToonException ex)
        {
            Check(ex.Code == "VOICE_RECONCILIATION_MISMATCH", "reconciliation requires exact terminal marker");
        }
        transport.DescriptionOverride = null;
        var reconciled = await studio.Voices.Reconcile(
            failed.Str("job_id"),
            transport.LastVoiceId,
            "Fixture matching operation description marker",
            CancellationToken.None
        );
        Check(reconciled.Str("state") == "pending_review", "unknown clone reconciled by description marker");
        var reused = await Execute(Clone(uncertain, "reuse-reconciled"));
        Check(
            reused["result"]!.Bool("reused") && transport.Clones == calls,
            "reconciled result reuses clone across new job key"
        );
        var longDescription = new string('x', 4000);
        var legacySource = await NewSource("Legacy metadata fixture", description: longDescription);
        transport.FailAfterDispatch = true;
        var legacyFailed = await Execute(Clone(legacySource, "legacy-unknown"), "failed", "PROVIDER_OUTCOME_UNKNOWN");
        transport.FailAfterDispatch = false;
        var legacyOperation = studio.Store.Job(legacyFailed.Str("job_id"))["snapshot"]!.Str("operation_key");
        Check(
            transport.LastDescription == longDescription + "\n\n[toon_request:" + legacyOperation + "]",
            "maximum local description length does not truncate original text or operation marker"
        );
        transport.LegacyLabelsOnly = true;
        var legacyReconciled = await studio.Voices.Reconcile(
            legacyFailed.Str("job_id"),
            transport.LastVoiceId,
            "Historical provider metadata contains only the legacy label",
            CancellationToken.None
        );
        Check(
            legacyReconciled.Str("state") == "pending_review"
                && legacyReconciled.Str("operation_key") == legacyOperation,
            "unknown clone remains recoverable from legacy full operation label without a description marker"
        );
        transport.LegacyLabelsOnly = false;
        calls = transport.Clones;
        Environment.SetEnvironmentVariable("TOON_MAX_VOICE_CLONES", "2");
        var limited = await NewSource("Budget fixture");
        await Execute(Clone(limited, "budget"), "failed", "VOICE_CLONE_LIMIT");
        Check(transport.Clones == calls, "durable clone budget prevents new HTTP requests");
        var cancelled = Clone(limited, "cancelled");
        studio.Cancel(cancelled.Str("job_id"));
        Check(
            studio.Store.Claim() == null && !studio.Job(cancelled.Str("job_id")).Items("artifacts").Any(),
            "queued clone cancellation sends no request"
        );
        var stale = Clone(limited, "stale");
        studio.Library.Update(limited.Str("item_id"), limited.Int("revision"), limited["submission"]!.DeepClone());
        await Execute(stale, "failed", "REVISION_CONFLICT");
        Environment.SetEnvironmentVariable("TOON_MAX_VOICE_CLONES", "10");
        var blocked = await NewSource("Cancelled in-flight fixture");
        transport.BlockAfterDispatch = true;
        var inFlight = Clone(blocked, "in-flight-cancel");
        var running = new Worker(studio).Execute(studio.Store.Claim()!, CancellationToken.None);
        await transport.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        studio.Cancel(inFlight.Str("job_id"));
        await running;
        Check(
            studio.Job(inFlight.Str("job_id")).Str("state") == "cancelled",
            "in-flight cancellation interrupts HTTP wait"
        );
        var afterCancellation = transport.Clones;
        transport.BlockAfterDispatch = false;
        await Execute(Clone(blocked, "after-cancel"), "failed", "PROVIDER_OUTCOME_UNKNOWN");
        Check(transport.Clones == afterCancellation, "cancelled dispatched clone cannot be silently repeated");

        var rejected = await NewSource("Rate limit fixture");
        transport.RateLimited = true;
        var rejectedJob = await Execute(Clone(rejected, "rate-limit"), "failed", "PROVIDER_ERROR");
        var rejectedJournal = studio.Store.Request(
            studio.Store.Job(rejectedJob.Str("job_id"))["snapshot"]!.Str("operation_key")
        )!;
        Check(
            rejectedJob["error"]!.Str("message").Contains("HTTP 429, rate_limit_exceeded", StringComparison.Ordinal)
                && rejectedJournal.Str("provider_status") == "rate_limit_exceeded"
                && rejectedJournal.Int("http_status") == 429,
            "safe provider status is exposed without the provider message"
        );
        Check(
            !rejectedJob.ToJsonString().Contains(VoiceTransport.PrivateProviderMessage, StringComparison.Ordinal)
                && !rejectedJournal
                    .ToJsonString()
                    .Contains(VoiceTransport.PrivateProviderMessage, StringComparison.Ordinal),
            "raw provider message is absent from job and journal"
        );
        var afterRateLimit = transport.Clones;
        transport.RateLimited = false;
        await Execute(Clone(rejected, "rate-limit-retry"), "failed", "PROVIDER_ERROR");
        Check(transport.Clones == afterRateLimit, "Retry-After prevents another HTTP request");

        const string errorSecret = "offline_fixture_secret_do_not_log";
        Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", errorSecret);
        foreach (
            var failure in new[]
            {
                (HttpStatusCode.BadRequest, "not-json " + VoiceTransport.PrivateProviderMessage),
                (
                    HttpStatusCode.BadRequest,
                    J.Node(new { detail = new { status = "unsafe code\n" + VoiceTransport.PrivateProviderMessage } })
                        .ToJsonString()
                ),
                (
                    HttpStatusCode.BadRequest,
                    J.Node(
                            new
                            {
                                detail = new { status = errorSecret, message = VoiceTransport.PrivateProviderMessage },
                            }
                        )
                        .ToJsonString()
                ),
                (HttpStatusCode.InternalServerError, "<html>" + VoiceTransport.PrivateProviderMessage + "</html>"),
            }
        )
        {
            var source = await NewSource("Invalid provider error fixture");
            transport.CloneErrorStatus = failure.Item1;
            transport.CloneErrorBody = failure.Item2;
            var code = failure.Item1 == HttpStatusCode.BadRequest ? "PROVIDER_ERROR" : "PROVIDER_OUTCOME_UNKNOWN";
            var errorJob = await Execute(
                Clone(source, "invalid-provider-body-" + source.Str("item_id")),
                "failed",
                code
            );
            var journal = studio.Store.Request(
                studio.Store.Job(errorJob.Str("job_id"))["snapshot"]!.Str("operation_key")
            )!;
            Check(
                journal.Str("state") == (failure.Item1 == HttpStatusCode.BadRequest ? "rejected" : "unknown")
                    && journal["provider_status"] == null
                    && !journal.ToJsonString().Contains(VoiceTransport.PrivateProviderMessage, StringComparison.Ordinal)
                    && !errorJob
                        .ToJsonString()
                        .Contains(VoiceTransport.PrivateProviderMessage, StringComparison.Ordinal)
                    && !journal.ToJsonString().Contains(errorSecret, StringComparison.Ordinal)
                    && !errorJob.ToJsonString().Contains(errorSecret, StringComparison.Ordinal),
                "malformed, unsafe or secret-valued provider status retains HTTP classification and hides body"
            );
        }
        transport.CloneErrorBody = null;
        Environment.SetEnvironmentVariable("ELEVENLABS_API_KEY", "offline-fixture-not-a-secret");

        try
        {
            await studio.Voices.Reconcile(
                failed.Str("job_id"),
                "unrelated_voice",
                "Wrong fixture voice",
                CancellationToken.None
            );
            throw new Exception("Expected reconciliation mismatch");
        }
        catch (ToonException ex)
        {
            Check(ex.Code == "VOICE_RECONCILIATION_MISMATCH", "reconciliation rejects unrelated provider voice");
        }
        var secondPreview = await Execute(
            studio.Voices.StartPreview(
                reconciled.Str("profile_id"),
                reconciled.Int("revision"),
                text,
                true,
                120,
                "second-profile-preview"
            )
        );
        var secondProfile = studio.Voices.Review(
            reconciled.Str("profile_id"),
            reconciled.Int("revision"),
            true,
            "Offline second voice fixture only"
        );
        var changedBinding = studio.Voices.Bind(
            "buyer",
            "1.0.0",
            1,
            secondProfile.Str("profile_id"),
            secondProfile.Int("revision")
        );
        Check(
            changedBinding.Int("revision") == 2
                && studio.Store.Project(saved.Str("project_id"))["project"]!["characters"]![0]!["voice_profile"]!.Str(
                    "profile_id"
                ) == approved.Str("profile_id"),
            "rebinding affects new projects and preserves saved project reference"
        );
        Check(
            Speech.CacheKey(text, studio.Voices.Resolve(changedBinding["voice_profile"]!))
                != Speech.CacheKey(
                    text,
                    studio.Store.Job(final.Str("job_id"))["snapshot"]!["voices"]!.AsObject().First().Value!
                ),
            "different voice identity has a different audio cache key"
        );
        var revoked = studio.Library.Get(uncertain.Str("item_id"));
        var revokedSubmission = revoked["submission"]!.DeepClone();
        revokedSubmission["details"]!["cloud_upload_consent"] = false;
        studio.Library.Update(revoked.Str("item_id"), revoked.Int("revision"), revokedSubmission);
        Reject(() => studio.Voices.Resolve(changedBinding["voice_profile"]!), "VOICE_CONSENT_REQUIRED");
        foreach (var completed in new[] { clone, preview, secondPreview })
        {
            Validation.Schema(
                studio.Job(completed.Str("job_id")),
                Path.Combine(config.Root, "contracts/job.schema.json")
            );
        }
        Check(true, "clone and preview jobs satisfy contract");
        var verificationSource = await NewSource("Verification settings fixture");
        transport.RequireVerification = true;
        var verificationClone = await Execute(Clone(verificationSource, "verification-settings"));
        var verificationProfile = verificationClone["result"]!["profile"]!;
        var verificationSettings = new VoiceSettings
        {
            Stability = .5,
            SimilarityBoost = .75,
            Style = 0,
            UseSpeakerBoost = true,
            Speed = 1,
        };
        var verificationRequests = transport.Requests;
        verificationProfile = studio.Voices.Configure(
            verificationProfile.Str("profile_id"),
            verificationProfile.Int("revision"),
            verificationSettings,
            "Configure without bypassing provider verification"
        );
        Check(
            verificationProfile.Bool("requires_verification")
                && verificationProfile.Str("state") == "verification_required"
                && transport.Requests == verificationRequests,
            "configuration preserves provider verification gate without HTTP"
        );
        transport.RequireVerification = false;
        var beforeSettings = studio.Voices.Profile(restoredProfile.Str("profile_id"));
        var immutableProfile = J.Canonical(beforeSettings);
        var previousDescriptor = studio.Voices.Resolve(
            J.Node(new { profile_id = beforeSettings.Str("profile_id"), revision = beforeSettings.Int("revision") })
        );
        var originalDescriptor = studio.Store.Job(final.Str("job_id"))["snapshot"]!["voices"]!
            .AsObject()
            .First()
            .Value!;
        Check(
            previousDescriptor["voice_settings"]!["speed"] == null
                && Speech.CacheKey(text, previousDescriptor) == Speech.CacheKey(text, originalDescriptor),
            "V4 profiles retain the same cache key for unchanged effective settings"
        );
        var settings = new VoiceSettings
        {
            Stability = .3,
            SimilarityBoost = .85,
            Style = .6,
            UseSpeakerBoost = true,
            Speed = .7,
        };
        var configurationRequests = transport.Requests;
        foreach (
            var invalid in new[]
            {
                settings with
                {
                    Stability = -.01,
                },
                settings with
                {
                    Stability = 1.01,
                },
                settings with
                {
                    SimilarityBoost = -.01,
                },
                settings with
                {
                    SimilarityBoost = 1.01,
                },
                settings with
                {
                    Style = -.01,
                },
                settings with
                {
                    Style = 1.01,
                },
                settings with
                {
                    Speed = .69,
                },
                settings with
                {
                    Speed = 1.21,
                },
                settings with
                {
                    Stability = double.NaN,
                },
                settings with
                {
                    Speed = double.PositiveInfinity,
                },
            }
        )
        {
            Reject(
                () =>
                    studio.Voices.Configure(
                        beforeSettings.Str("profile_id"),
                        beforeSettings.Int("revision"),
                        invalid,
                        "Invalid settings fixture"
                    ),
                "VALIDATION_ERROR"
            );
        }
        Reject(
            () => studio.Voices.Configure(beforeSettings.Str("profile_id"), 0, settings, "Invalid revision"),
            "VALIDATION_ERROR"
        );
        Reject(
            () =>
                studio.Voices.Configure(
                    beforeSettings.Str("profile_id"),
                    beforeSettings.Int("revision"),
                    settings,
                    " "
                ),
            "VALIDATION_ERROR"
        );
        var configured = studio.Voices.Configure(
            beforeSettings.Str("profile_id"),
            beforeSettings.Int("revision"),
            settings,
            "Slower delivery fixture; no similarity claim"
        );
        Check(
            configured.Int("revision") == beforeSettings.Int("revision") + 1
                && configured.Str("state") == "pending_review"
                && configured["review_note"] == null
                && J.Canonical(configured["voice_settings"]!) == J.Canonical(J.Node(settings))
                && J.Canonical(studio.Voices.Profile(beforeSettings.Str("profile_id"), beforeSettings.Int("revision")))
                    == immutableProfile,
            "configuration resets approval and preserves immutable previous profile"
        );
        Reject(
            () =>
                studio.Voices.Configure(
                    beforeSettings.Str("profile_id"),
                    beforeSettings.Int("revision"),
                    settings,
                    "Stale revision"
                ),
            "REVISION_CONFLICT"
        );
        Reject(
            () => studio.Voices.Review(configured.Str("profile_id"), configured.Int("revision"), true, "Old preview"),
            "VOICE_PREVIEW_REQUIRED"
        );
        Reject(
            () =>
                studio.Voices.StartPreview(
                    configured.Str("profile_id"),
                    configured.Int("revision"),
                    text,
                    false,
                    0,
                    "settings-require-new-audio"
                ),
            "TTS_LIMIT_EXCEEDED"
        );
        var settingsPreview = studio.Voices.StartPreview(
            configured.Str("profile_id"),
            configured.Int("revision"),
            text,
            true,
            120,
            "settings-descriptor-fixture"
        );
        var configuredDescriptor = studio.Store.Job(settingsPreview.Str("job_id"))["snapshot"]!["voice"]!;
        Check(
            J.Canonical(configuredDescriptor["voice_settings"]!) == J.Canonical(Speech.V4Settings(J.Node(settings)))
                && Speech.CacheKey(text, configuredDescriptor) != Speech.CacheKey(text, previousDescriptor),
            "configured delivery is pinned into synthesis descriptor and changes audio cache key"
        );
        studio.Cancel(settingsPreview.Str("job_id"));
        foreach (
            var edge in new[]
            {
                settings with
                {
                    Stability = 0,
                    SimilarityBoost = 0,
                    Style = 0,
                    UseSpeakerBoost = false,
                    Speed = .7,
                },
                settings with
                {
                    Stability = 1,
                    SimilarityBoost = 1,
                    Style = 1,
                    Speed = 1.2,
                },
            }
        )
        {
            configured = studio.Voices.Configure(
                configured.Str("profile_id"),
                configured.Int("revision"),
                edge,
                "Inclusive range boundary fixture"
            );
        }
        Check(transport.Requests == configurationRequests, "configuration and validation perform no HTTP requests");
        Check(
            transport.MultipartValid,
            "multipart endpoint, original bytes, filename and description marker verified; custom labels omitted"
        );
        J.Write(
            Path.Combine(config.Data, "voice-acceptance.json"),
            J.Node(
                new
                {
                    checks,
                    final_job = final.Str("job_id"),
                    project_id = saved.Str("project_id"),
                    provider_transport = "injected offline HTTP fixture; no actual ElevenLabs clone",
                    live_clone = "pending actual sample, API key and explicit budget",
                    human_listening = "pending",
                }
            )
        );
        Console.WriteLine("REPORT " + Path.Combine(config.Data, "voice-acceptance.json"));
    }

    internal sealed class VoiceTransport(
        byte[] speech,
        byte[] originalSample,
        IReadOnlyDictionary<string, byte[]>? speechByText = null
    ) : HttpMessageHandler
    {
        public const string PrivateProviderMessage = "offline-private-provider-message-do-not-log";
        public int Requests { get; private set; }
        public int Clones { get; private set; }
        public int Syntheses { get; private set; }
        public bool RequireVerification { get; set; }
        public bool FailAfterDispatch { get; set; }
        public bool BlockAfterDispatch { get; set; }
        public bool RateLimited { get; set; }
        public string? CloneErrorBody { get; set; }
        public HttpStatusCode CloneErrorStatus { get; set; }
        public TaskCompletionSource Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool MultipartValid { get; private set; }
        public string LastVoiceId { get; private set; } = "";
        public string LastDescription { get; private set; } = "";
        public string? DescriptionOverride { get; set; }
        public bool LegacyLabelsOnly { get; set; }
        private readonly Dictionary<string, string> descriptions = [];
        private readonly Dictionary<string, string> operations = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.RequestUri!.Host != "api.elevenlabs.io")
                throw new Exception("Offline fixture rejected unknown host");
            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v1/voices/add")
            {
                Clones++;
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p =>
                    p.Headers.ContentDisposition!.Name!.Trim('"')
                );
                var bytes = await parts["files"].ReadAsByteArrayAsync(ct);
                LastDescription = await parts["description"].ReadAsStringAsync(ct);
                var marker = Regex.Match(
                    LastDescription,
                    "\\n\\n\\[toon_request:(clone-[0-9a-f]{64})\\]\\z",
                    RegexOptions.CultureInvariant
                );
                MultipartValid =
                    bytes.AsSpan().SequenceEqual(originalSample)
                    && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                    && parts["files"].Headers.ContentDisposition!.FileName!.Trim('"') == "sample.wav"
                    && await parts["remove_background_noise"].ReadAsStringAsync(ct) == "false"
                    && !parts.ContainsKey("labels")
                    && marker.Success
                    && !LastDescription.Contains(
                        request.Headers.GetValues("xi-api-key").Single(),
                        StringComparison.Ordinal
                    )
                    && !LastDescription.Contains(PrivateProviderMessage, StringComparison.Ordinal);
                if (!MultipartValid)
                    throw new Exception("Offline clone fixture rejected invalid multipart metadata");
                LastVoiceId = "fixture_voice_" + Clones;
                descriptions[LastVoiceId] = LastDescription;
                operations[LastVoiceId] = marker.Groups[1].Value;
                if (BlockAfterDispatch)
                {
                    Dispatched.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }

                if (RateLimited)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        Content = new StringContent(
                            J.Node(
                                    new
                                    {
                                        detail = new
                                        {
                                            status = "rate_limit_exceeded",
                                            message = PrivateProviderMessage,
                                        },
                                    }
                                )
                                .ToJsonString(),
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.FromMinutes(5)
                    );
                    return limited;
                }

                if (CloneErrorBody != null)
                    return new HttpResponseMessage(CloneErrorStatus) { Content = new StringContent(CloneErrorBody) };

                if (FailAfterDispatch)
                    throw new HttpRequestException("Offline fixture disconnect");
                return Json(new { voice_id = LastVoiceId, requires_verification = RequireVerification });
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/voices/", StringComparison.Ordinal))
            {
                var id = path.Split('/').Last();
                return Json(
                    new
                    {
                        voice_id = id,
                        description = LegacyLabelsOnly
                            ? null
                            : DescriptionOverride ?? descriptions.GetValueOrDefault(id, "Unrelated voice"),
                        labels = LegacyLabelsOnly
                            ? new { toon_request = operations.GetValueOrDefault(id, "wrong-label") }
                            : null,
                        voice_verification = new
                        {
                            requires_verification = RequireVerification,
                            is_verified = !RequireVerification,
                        },
                    }
                );
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/with-timestamps", StringComparison.Ordinal))
            {
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                if (
                    path != "/v1/text-to-dialogue/with-timestamps"
                    || payload.Str("model_id") != "eleven_v4"
                    || payload.Str("language_code") != "ru"
                    || payload["text"] != null
                    || payload["voice_settings"] != null
                    || payload["inputs"]!.AsArray().Count != 1
                    || payload["settings"]?["stability"] == null
                    || payload["settings"]?["similarity"] == null
                    || payload["settings"]!.AsObject().Count != 2
                )
                {
                    throw new Exception("Expected V4 dialogue payload with only supported settings");
                }
                Syntheses++;
                var bytes = speech;
                if (speechByText != null)
                {
                    var text = payload["inputs"]![0]!.Str("text");
                    bytes = speechByText[text];
                }
                return Json(new { audio_base64 = Convert.ToBase64String(bytes) });
            }
            throw new Exception("Offline fixture rejected unknown endpoint");
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(J.Node(body).ToJsonString(), Encoding.UTF8, "application/json"),
            };
    }
}
