using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Toon.Core;
using Toon.Infrastructure;

var config = new Config();
var studio = new Studio(config);
var api = new ToonTools(studio);
var outputSchema = JsonDocument
    .Parse(
        """
{"type":"object","oneOf":[{"required":["ok","data"],"properties":{"ok":{"const":true},"data":{}},"additionalProperties":false},{"required":["ok","error"],"properties":{"ok":{"const":false},"error":{"type":"object","required":["code","message","retryable","details"],"properties":{"code":{"type":"string"},"message":{"type":"string"},"retryable":{"type":"boolean"},"details":{"type":"object"}},"additionalProperties":false}},"additionalProperties":false}]}
"""
    )
    .RootElement;
var tools = typeof(ToonTools)
    .GetMethods()
    .Where(m => m.GetCustomAttributes(typeof(McpServerToolAttribute), false).Length > 0)
    .Select(m =>
        McpServerTool.Create(
            m,
            api,
            new McpServerToolCreateOptions { UseStructuredContent = true, OutputSchema = outputSchema }
        )
    )
    .ToArray();
foreach (var tool in tools)
{
    var input = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
    input["additionalProperties"] = false;
    if (tool.ProtocolTool.Name is "create_project" or "update_project")
    {
        var draft = J.Read(Path.Combine(config.Root, "contracts/project.schema.json"));
        input["$defs"] = draft["$defs"]!.DeepClone();
        draft.AsObject().Remove("$defs");
        draft.AsObject().Remove("$id");
        draft.AsObject().Remove("$schema");
        input["properties"]!["project"] = draft;
    }

    if (tool.ProtocolTool.Name == "start_render")
    {
        input["properties"]!["quality"] = J.Node(new { type = "string", @enum = new[] { "preview", "final" } });
    }

    if (tool.ProtocolTool.Name is "create_library_item" or "update_library_item")
    {
        var submission = J.Read(Path.Combine(config.Root, "contracts/library-submission.schema.json"));
        input["$defs"] = submission["$defs"]!.DeepClone();
        submission.AsObject().Remove("$defs");
        submission.AsObject().Remove("$id");
        submission.AsObject().Remove("$schema");
        input["properties"]!["submission"] = submission;
    }

    if (tool.ProtocolTool.Name is "set_character_recipe" or "set_environment_recipe")
    {
        var schema = tool.ProtocolTool.Name == "set_character_recipe" ? "character-recipe" : "environment-recipe";
        var recipe = J.Read(Path.Combine(config.Root, "contracts", schema + ".schema.json"));
        input["$defs"] = recipe["$defs"]!.DeepClone();
        recipe.AsObject().Remove("$defs");
        recipe.AsObject().Remove("$id");
        recipe.AsObject().Remove("$schema");
        input["properties"]!["recipe"] = recipe;
    }
    tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(input);
}
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(tools).WithResources(new ToonResources(studio));
await builder.Build().RunAsync();

public sealed class ToonTools(Studio studio)
{
    [McpServerTool(UseStructuredContent = true, Name = "get_content_workflow", ReadOnly = true)]
    [Description(
        "Read the complete MCP workflow for character, environment, voice or production: ordered tools, requirements, review gates, revisions, cancellation and explicit retry rules. Does not create jobs or call cloud services."
    )]
    public Task<CallToolResult> ContentWorkflow(string kind) => Call(() => ContentWorkflows.Get(kind));

    [McpServerTool(UseStructuredContent = true, Name = "start_voice_clone", Destructive = false, Idempotent = true)]
    [Description(
        "Queue one ElevenLabs Instant Voice Clone in the persistent worker. Uploads the original sample only with submission rights/consents, completed technical preparation, explicit sample listening review, allow_cloud_upload=true and max_clone_requests=1. Server also requires TOON_ALLOW_VOICE_CLONING=1, TOON_MAX_VOICE_CLONES and ELEVENLABS_API_KEY. Never automatically retry uncertain outcomes. No TTS is generated here."
    )]
    public Task<CallToolResult> CloneVoice(
        string item_id,
        int expected_revision,
        bool allow_cloud_upload,
        int max_clone_requests,
        bool sample_reviewed,
        string review_note,
        string idempotency_key
    ) =>
        Call(() =>
            studio.Voices.StartClone(
                item_id,
                expected_revision,
                allow_cloud_upload,
                max_clone_requests,
                sample_reviewed,
                review_note,
                idempotency_key
            )
        );

    [McpServerTool(UseStructuredContent = true, Name = "get_voice_profile", ReadOnly = true)]
    [Description(
        "Read latest or immutable voice profile revision. States distinguish provider verification, pending listening review, approved and rejected. Provider IDs are not proof of similarity."
    )]
    public Task<CallToolResult> VoiceProfile(string profile_id, int? revision = null) =>
        Call(() => studio.Voices.Profile(profile_id, revision));

    [McpServerTool(UseStructuredContent = true, Name = "list_voice_profiles", ReadOnly = true)]
    public Task<CallToolResult> VoiceProfiles() => Call(() => studio.Voices.Profiles());

    [McpServerTool(UseStructuredContent = true, Name = "configure_voice_profile", Destructive = false)]
    [Description(
        "Save local voice settings as a new immutable profile revision. Does not call ElevenLabs or clone again. Resets approval to pending_review; generate and listen to a new preview before approval. Existing revisions and project references remain unchanged."
    )]
    public Task<CallToolResult> ConfigureVoice(
        string profile_id,
        int expected_revision,
        VoiceSettings voice_settings,
        string note
    ) => Call(() => studio.Voices.Configure(profile_id, expected_revision, voice_settings, note));

    [McpServerTool(UseStructuredContent = true, Name = "refresh_voice_verification", Destructive = false)]
    [Description(
        "Read ElevenLabs verification status for this voice. Uses a metadata GET, never bypasses verification or creates another clone. Missing verification fields keep the profile blocked."
    )]
    public Task<CallToolResult> RefreshVoice(string profile_id, int expected_revision, CancellationToken ct) =>
        Call(() => studio.Voices.Refresh(profile_id, expected_revision, ct));

    [McpServerTool(UseStructuredContent = true, Name = "reconcile_voice_clone", Destructive = false)]
    [Description(
        "Recover a provider voice ID after a failed/cancelled clone job. Performs metadata GET and requires an exact Toon description marker or legacy operation label; never repeats the clone POST. Supply the ID found in the operator's ElevenLabs account."
    )]
    public Task<CallToolResult> ReconcileVoice(
        string job_id,
        string provider_voice_id,
        string note,
        CancellationToken ct
    ) => Call(() => studio.Voices.Reconcile(job_id, provider_voice_id, note, ct));

    [McpServerTool(UseStructuredContent = true, Name = "start_voice_preview", Destructive = false, Idempotent = true)]
    [Description(
        "Generate a short WAV for listening review in the persistent worker. Requires a verified provider voice and explicit allow_new_tts/character limit for uncached speech. Does not assert similarity; listen and compare with original before review_voice_profile."
    )]
    public Task<CallToolResult> PreviewVoice(
        string profile_id,
        int expected_revision,
        string text,
        string idempotency_key,
        bool allow_new_tts = false,
        int max_new_tts_characters = 0
    ) =>
        Call(() =>
            studio.Voices.StartPreview(
                profile_id,
                expected_revision,
                text,
                allow_new_tts,
                max_new_tts_characters,
                idempotency_key
            )
        );

    [McpServerTool(UseStructuredContent = true, Name = "review_voice_profile", Destructive = false)]
    [Description(
        "Record the operator's listening decision as a new immutable profile revision. Approval requires a verified preview of the current revision. Rejecting disables use, including old project references. Does not delete the cloud voice."
    )]
    public Task<CallToolResult> ReviewVoice(
        string profile_id,
        int expected_revision,
        bool approved,
        string review_note
    ) => Call(() => studio.Voices.Review(profile_id, expected_revision, approved, review_note));

    [McpServerTool(UseStructuredContent = true, Name = "get_character_voice", ReadOnly = true)]
    public Task<CallToolResult> CharacterVoice(string character_id, string version) =>
        Call(() => studio.Voices.Binding(character_id, version) ?? J.Node(new { bound = false }));

    [McpServerTool(UseStructuredContent = true, Name = "bind_character_voice", Destructive = false)]
    [Description(
        "Bind an approved voice profile revision to a published character version, using expected_binding_revision=0 for first binding. New projects with audio.voice_alias=character pin this default as characters[].voice_profile; existing project revisions remain unchanged. Explicit legacy aliases still override it."
    )]
    public Task<CallToolResult> BindVoice(
        string character_id,
        string version,
        int expected_binding_revision,
        string profile_id,
        int profile_revision
    ) => Call(() => studio.Voices.Bind(character_id, version, expected_binding_revision, profile_id, profile_revision));

    [McpServerTool(UseStructuredContent = true, Name = "get_environment_recipe_template", ReadOnly = true)]
    [Description(
        "Get example layer masks, character slots, depth ordering, anchors and camera presets. Coordinates match the example workshop only; annotate real supplied sketches before preparation."
    )]
    public Task<CallToolResult> EnvironmentRecipeTemplate() =>
        Call(() => J.Read(Path.Combine(studio.Config.Root, "templates/library/environment-recipe.json")));

    [McpServerTool(UseStructuredContent = true, Name = "set_environment_recipe", Destructive = false)]
    [Description(
        "Save a versioned environment preparation recipe. Safe vector nodes and cutouts over imported source roles; no external URLs, scripts or paths. Declare missing and generated details. Original images are preserved."
    )]
    public Task<CallToolResult> SetEnvironmentRecipe(string item_id, int expected_revision, JsonObject recipe) =>
        Call(() => studio.Library.SetEnvironmentRecipe(item_id, expected_revision, recipe));

    [McpServerTool(
        UseStructuredContent = true,
        Name = "start_environment_test",
        Destructive = false,
        Idempotent = true
    )]
    [Description(
        "Queue an offline verified environment MP4 for this exact prepared revision, with two original control characters, wide and both close-ups. Supply a real 4–12 second recording and exact transcript; recording repeats in three scenes. Review foreground masks, overlap and scale before local publication."
    )]
    public Task<CallToolResult> StartEnvironmentTest(
        string item_id,
        int expected_revision,
        JsonObject audio_asset,
        string transcript,
        string idempotency_key
    ) => Call(() => studio.StartEnvironmentTest(item_id, expected_revision, audio_asset, transcript, idempotency_key));

    [McpServerTool(UseStructuredContent = true, Name = "get_character_recipe_template", ReadOnly = true)]
    [Description(
        "Get a local annotated cutout recipe example. Coordinates refer to the example sheet, not automatic detection. Agent/operator annotates the supplied sketches; missing and generated details must be declared."
    )]
    public Task<CallToolResult> CharacterRecipeTemplate() =>
        Call(() => J.Read(Path.Combine(studio.Config.Root, "templates/library/character-recipe.json")));

    [McpServerTool(UseStructuredContent = true, Name = "set_character_recipe", Destructive = false)]
    [Description(
        "Save a character preparation recipe in a new draft revision. Safe vector geometry and polygons over imported source roles only; no scripts, SVG files, URLs or external paths. Does not modify original sketches."
    )]
    public Task<CallToolResult> SetCharacterRecipe(string item_id, int expected_revision, JsonObject recipe) =>
        Call(() => studio.Library.SetCharacterRecipe(item_id, expected_revision, recipe));

    [McpServerTool(UseStructuredContent = true, Name = "start_character_test", Destructive = false, Idempotent = true)]
    [Description(
        "Queue a verified character test MP4 for the exact prepared revision, with wide/close shots, six emotions, gestures, phone and a real 4–12 second recording repeated in three scenes. Provide its transcript. Offline, no TTS requests. Inspect the resulting video before publication."
    )]
    public Task<CallToolResult> StartCharacterTest(
        string item_id,
        int expected_revision,
        JsonObject audio_asset,
        string transcript,
        string idempotency_key
    ) => Call(() => studio.StartCharacterTest(item_id, expected_revision, audio_asset, transcript, idempotency_key));

    private static async Task<CallToolResult> Call(Func<Task<JsonNode>> operation)
    {
        JsonNode result;
        bool isError = false;
        try
        {
            result = J.Node(new { ok = true, data = await operation() });
        }
        catch (Exception ex)
        {
            isError = true;
            var e = ex as ToonException;
            result = J.Node(
                new
                {
                    ok = false,
                    error = new
                    {
                        code = e?.Code ?? "VALIDATION_ERROR",
                        message = e?.Message ?? "Request could not be completed; verify input and local dependencies",
                        retryable = e?.Retryable ?? false,
                        details = new { pointer = e?.Pointer ?? "" },
                    },
                }
            );
        }

        var content = new List<ContentBlock> { new TextContentBlock { Text = result.ToJsonString() } };
        if (!isError && result["data"] is JsonArray artifacts)
        {
            foreach (var item in artifacts.Where(x => x?["resource_uri"] != null))
            {
                var a = item!;
                content.Add(
                    new ResourceLinkBlock
                    {
                        Uri = a.Str("resource_uri"),
                        Name = a.Str("name"),
                        MimeType = a.Str("mime"),
                    }
                );
            }
        }
        return new CallToolResult
        {
            IsError = isError,
            StructuredContent = JsonSerializer.SerializeToElement(result),
            Content = content,
        };
    }

    private static Task<CallToolResult> Call(Func<JsonNode> operation) => Call(() => Task.FromResult(operation()));

    [
        McpServerTool(UseStructuredContent = true, Name = "studio_doctor", ReadOnly = true),
        Description(
            "Check local runtimes, renderer, fonts, worker heartbeat and voice readiness. Never returns secrets."
        )
    ]
    public Task<CallToolResult> Doctor(CancellationToken cancellationToken) =>
        Call(() => studio.Doctor(cancellationToken));

    [
        McpServerTool(UseStructuredContent = true, Name = "get_catalog", ReadOnly = true),
        Description(
            "Read render-compatible styles, characters, backgrounds, gestures or voices. kind=library lists source-package heads; use list_library_items for filters."
        )
    ]
    public Task<CallToolResult> Catalog(
        [Description("styles, characters, backgrounds, gestures, voices or library")] string kind
    ) => Call(() => studio.Catalog(kind));

    [
        McpServerTool(UseStructuredContent = true, Name = "register_asset", Destructive = false),
        Description(
            "Import PNG/JPEG/WAV/MP3 from operator-configured TOON_IMPORT_ROOTS. Copy and hash immutable bytes. Optional JSON word alignment or phrase SRT; illustrative timing is rejected."
        )
    ]
    public Task<CallToolResult> Register(
        string source_path,
        string kind,
        string? alignment_path = null,
        CancellationToken cancellationToken = default
    ) => Call(() => studio.Assets.Register(source_path, kind, alignment_path, cancellationToken));

    [
        McpServerTool(UseStructuredContent = true, Name = "create_project", Destructive = false),
        Description(
            "Persist a DraftProject 1.0.0 as revision 1; structural/semantic errors fail, missing external dependencies may remain draft. Read the style guide first."
        )
    ]
    public Task<CallToolResult> Create(JsonNode project) => Call(() => studio.Create(project));

    [
        McpServerTool(UseStructuredContent = true, Name = "get_project", ReadOnly = true),
        Description("Read saved project revision and readiness. Omit revision to read latest.")
    ]
    public Task<CallToolResult> Get(string project_id, int? revision = null) =>
        Call(() => studio.GetProject(project_id, revision));

    [
        McpServerTool(UseStructuredContent = true, Name = "update_project", Destructive = false),
        Description(
            "Replace the complete draft, creating an immutable revision. expected_revision must equal current or REVISION_CONFLICT is returned."
        )
    ]
    public Task<CallToolResult> Update(string project_id, int expected_revision, JsonNode project) =>
        Call(() => studio.Update(project_id, expected_revision, project));

    [
        McpServerTool(UseStructuredContent = true, Name = "validate_project", ReadOnly = true),
        Description(
            "Check dependencies and estimate uncached TTS Unicode code points, without paid calls. Final timing/layout is checked after decoded speech preparation."
        )
    ]
    public Task<CallToolResult> Validate(string project_id, int revision) =>
        Call(() => studio.Validate(project_id, revision));

    [
        McpServerTool(UseStructuredContent = true, Name = "start_render", Destructive = false, Idempotent = true),
        Description(
            "Enqueue preview/final in independent worker. Same key+request returns same job; changed request conflicts. New paid speech is disabled unless explicitly allowed with a positive character limit. Does not wait for rendering."
        )
    ]
    public Task<CallToolResult> Start(
        string project_id,
        int revision,
        string quality,
        string idempotency_key,
        bool allow_new_tts = false,
        int max_new_tts_characters = 0
    ) =>
        Call(() => studio.Start(project_id, revision, quality, idempotency_key, allow_new_tts, max_new_tts_characters));

    [
        McpServerTool(UseStructuredContent = true, Name = "get_job", ReadOnly = true),
        Description(
            "Poll durable job state/stage, usage and artifacts. A failed job is a successful poll, not a tool error."
        )
    ]
    public Task<CallToolResult> Job(string job_id) => Call(() => studio.Job(job_id));

    [
        McpServerTool(UseStructuredContent = true, Name = "cancel_job", Idempotent = true),
        Description(
            "Cancel queued work immediately or request controlled process-tree cancellation. Terminal states stay unchanged."
        )
    ]
    public Task<CallToolResult> Cancel(string job_id) => Call(() => studio.Cancel(job_id));

    [
        McpServerTool(UseStructuredContent = true, Name = "get_artifacts", ReadOnly = true),
        Description(
            "Return only published, hash-verified artifacts: MP4, PNG, project, timeline, captions, report and final ZIP. Never embeds media as base64."
        )
    ]
    public Task<CallToolResult> Artifacts(string job_id) => Call(() => studio.Artifacts(job_id));

    [
        McpServerTool(UseStructuredContent = true, Name = "get_submission_template", ReadOnly = true),
        Description(
            "Get a character, environment or voice submission template, required material roles and actual server import limits. No cloning, generation or paid requests."
        )
    ]
    public Task<CallToolResult> SubmissionTemplate(string kind) =>
        Call(() => SubmissionTemplates.Get(studio.Config, kind));

    [
        McpServerTool(UseStructuredContent = true, Name = "create_library_item", Destructive = false),
        Description(
            "Create a versioned content draft from a submission. Names need not be unique; use the returned stable item_id. No rendering or cloud calls."
        )
    ]
    public Task<CallToolResult> CreateLibrary(JsonNode submission) => Call(() => studio.Library.Create(submission));

    [
        McpServerTool(UseStructuredContent = true, Name = "update_library_item", Destructive = false),
        Description(
            "Replace submission metadata using expected_revision. Creates a new draft revision and invalidates previous review; published versions remain immutable. Kind cannot change."
        )
    ]
    public Task<CallToolResult> UpdateLibrary(string item_id, int expected_revision, JsonNode submission) =>
        Call(() => studio.Library.Update(item_id, expected_revision, submission));

    [
        McpServerTool(UseStructuredContent = true, Name = "import_library_material", Destructive = false),
        Description(
            "Import one PNG/JPEG or WAV/MP3 into a named material role from TOON_IMPORT_ROOTS. Preserve originals by hash. Replacing a role creates a new draft revision. No archive, SVG, code or cloud import."
        )
    ]
    public Task<CallToolResult> ImportLibrary(
        string item_id,
        int expected_revision,
        string role,
        string source_path,
        string note = "",
        CancellationToken cancellationToken = default
    ) => Call(() => studio.Library.Import(item_id, expected_revision, role, source_path, note, cancellationToken));

    [
        McpServerTool(UseStructuredContent = true, Name = "remove_library_material", Destructive = false),
        Description(
            "Remove a role from a new draft revision; immutable source bytes and old revisions remain available."
        )
    ]
    public Task<CallToolResult> RemoveLibraryMaterial(string item_id, int expected_revision, string role) =>
        Call(() => studio.Library.RemoveMaterial(item_id, expected_revision, role));

    [
        McpServerTool(UseStructuredContent = true, Name = "get_library_item", ReadOnly = true),
        Description(
            "Read current item or exact immutable revision/published version, including source references and preparation issues. Choose revision or version, not both."
        )
    ]
    public Task<CallToolResult> GetLibrary(string item_id, int? revision = null, string? version = null) =>
        Call(() => studio.Library.Get(item_id, revision, version));

    [
        McpServerTool(UseStructuredContent = true, Name = "get_library_materials", ReadOnly = true),
        Description(
            "Return hash-verified local file links to original images or voice samples of an exact library revision, for visual/listening review. No generated artwork or cloned voice is implied."
        )
    ]
    public Task<CallToolResult> LibraryMaterials(string item_id, int revision) =>
        Call(() => studio.Library.Materials(item_id, revision));

    [
        McpServerTool(UseStructuredContent = true, Name = "list_library_versions", ReadOnly = true),
        Description("List immutable published versions and their revision numbers, including archived items.")
    ]
    public Task<CallToolResult> LibraryVersions(string item_id) => Call(() => studio.Library.Versions(item_id));

    [
        McpServerTool(UseStructuredContent = true, Name = "list_library_items", ReadOnly = true),
        Description(
            "List library heads by optional kind (character/environment/voice) and state (draft/preparing/review/published/archived). Inspect render_supported; a source package alone is not renderable."
        )
    ]
    public Task<CallToolResult> ListLibrary(
        string? kind = null,
        string? state = null,
        int limit = 50,
        int offset = 0
    ) => Call(() => studio.Library.List(kind, state, limit, offset));

    [
        McpServerTool(
            UseStructuredContent = true,
            Name = "prepare_library_item",
            Destructive = false,
            Idempotent = true
        ),
        Description(
            "Queue local material checks and character/environment recipe preparation in the independent persistent worker. Same key/request returns same job. Inspect ready_for_publication and render_supported. No automatic reconstruction, voice cloning or paid calls."
        )
    ]
    public Task<CallToolResult> PrepareLibrary(string item_id, int expected_revision, string idempotency_key) =>
        Call(() => studio.Library.StartPreparation(item_id, expected_revision, idempotency_key));

    [
        McpServerTool(UseStructuredContent = true, Name = "publish_library_item", Destructive = false),
        Description(
            "Publish a reviewed package in the LOCAL library. Requires ready_for_publication, review_approved=true, review_note and a new increasing version. A renderable character or environment also requires a verified test MP4 for this exact package. Does not clone voices."
        )
    ]
    public Task<CallToolResult> PublishLibrary(
        string item_id,
        int expected_revision,
        string version,
        bool review_approved,
        string review_note
    ) => Call(() => studio.Library.Publish(item_id, expected_revision, version, review_approved, review_note));

    [
        McpServerTool(UseStructuredContent = true, Name = "archive_library_item", Destructive = false),
        Description(
            "Archive the current library head without deleting any revision, published version or original asset."
        )
    ]
    public Task<CallToolResult> ArchiveLibrary(string item_id, int expected_revision) =>
        Call(() => studio.Library.Archive(item_id, expected_revision));
}

public sealed class ToonResources(Studio studio)
{
    [McpServerResource(
        UriTemplate = "toon://workflows/{kind}",
        Name = "content_workflow",
        MimeType = "application/json"
    )]
    public string ContentWorkflow(string kind) => ContentWorkflows.Get(kind).ToJsonString();

    [McpServerResource(
        UriTemplate = "toon://guides/content-workflow",
        Name = "content_workflow_guide",
        MimeType = "text/markdown"
    )]
    public string ContentWorkflowGuide() =>
        File.ReadAllText(Path.Combine(studio.Config.Root, "docs/CONTENT-WORKFLOW.md"));

    [McpServerResource(
        UriTemplate = "toon://voices/{id}/revisions/{revision}",
        Name = "voice_profile_revision",
        MimeType = "application/json"
    )]
    public string VoiceProfileRevision(string id, int revision) => studio.Voices.Profile(id, revision).ToJsonString();

    [McpServerResource(
        UriTemplate = "toon://environments/schema",
        Name = "environment_schema",
        MimeType = "application/json"
    )]
    public string EnvironmentSchema() =>
        File.ReadAllText(Path.Combine(studio.Config.Root, "contracts/environment.schema.json"));

    [McpServerResource(
        UriTemplate = "toon://environments/recipe-schema",
        Name = "environment_recipe_schema",
        MimeType = "application/json"
    )]
    public string EnvironmentRecipeSchema() =>
        File.ReadAllText(Path.Combine(studio.Config.Root, "contracts/environment-recipe.schema.json"));

    [McpServerResource(
        UriTemplate = "toon://characters/rig-schema",
        Name = "character_rig_schema",
        MimeType = "application/json"
    )]
    public string CharacterRigSchema() =>
        File.ReadAllText(Path.Combine(studio.Config.Root, "contracts/character-rig.schema.json"));

    [McpServerResource(
        UriTemplate = "toon://characters/recipe-schema",
        Name = "character_recipe_schema",
        MimeType = "application/json"
    )]
    public string CharacterRecipeSchema() =>
        File.ReadAllText(Path.Combine(studio.Config.Root, "contracts/character-recipe.schema.json"));

    [
        McpServerResource(
            UriTemplate = "toon://library/templates/{kind}/{version}",
            Name = "submission_template",
            MimeType = "application/json"
        ),
        Description("Versioned submission template and server import limits")
    ]
    public string SubmissionTemplate(string kind, string version)
    {
        J.Require(version == "1.0.0", "LIBRARY_VERSION_NOT_FOUND", "Unknown template version");

        return SubmissionTemplates.Get(studio.Config, kind).ToJsonString();
    }

    [
        McpServerResource(
            UriTemplate = "toon://library/submission-schema",
            Name = "submission_schema",
            MimeType = "application/json"
        ),
        Description("Library submission JSON Schema")
    ]
    public string SubmissionSchema() =>
        File.ReadAllText(Path.Combine(studio.Config.Root, "contracts/library-submission.schema.json"));

    [
        McpServerResource(
            UriTemplate = "toon://library/{id}/revisions/{revision}",
            Name = "library_revision",
            MimeType = "application/json"
        ),
        Description("Hash-verified immutable library revision")
    ]
    public string LibraryRevision(string id, int revision) => studio.Library.Get(id, revision).ToJsonString();

    [
        McpServerResource(
            UriTemplate = "toon://library/{id}/revisions/{revision}/package",
            Name = "library_package",
            MimeType = "application/json"
        ),
        Description("Hash-verified preparation package, errors and source assets")
    ]
    public string LibraryPackage(string id, int revision) => studio.Library.Package(id, revision).ToJsonString();

    [
        McpServerResource(
            UriTemplate = "toon://styles/{id}/{version}/guide",
            Name = "style_guide",
            MimeType = "text/markdown"
        ),
        Description("Versioned original style and directing rules")
    ]
    public string Guide(string id, string version)
    {
        J.Require(id == "warm_editorial_2d" && version == "1.0.0", "ASSET_MISSING", "Unknown style guide");

        return File.ReadAllText(Path.Combine(studio.Config.Root, "styles/warm_editorial_2d/DIRECTING.md"));
    }

    [
        McpServerResource(
            UriTemplate = "toon://projects/{id}/revisions/{revision}",
            Name = "project_revision",
            MimeType = "application/json"
        ),
        Description("Immutable saved draft revision")
    ]
    public string Project(string id, int revision) => studio.Store.Project(id, revision).ToJsonString();

    [
        McpServerResource(UriTemplate = "toon://jobs/{id}/report", Name = "job_report", MimeType = "application/json"),
        Description("Verified report or current job state")
    ]
    public string Report(string id)
    {
        var job = studio.Store.Job(id);
        var report = job.Items("artifacts").FirstOrDefault(a => a.Str("name") == "validation-report.json");

        return report == null ? studio.Job(id).ToJsonString() : File.ReadAllText(report.Str("local_path"));
    }
}
