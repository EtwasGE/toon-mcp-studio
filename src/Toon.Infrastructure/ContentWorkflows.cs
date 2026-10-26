using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public static class ContentWorkflows
{
    public static string[] Tools(string kind) =>
        kind switch
        {
            "character" =>
            [
                "get_submission_template",
                "get_character_recipe_template",
                "create_library_item",
                "import_library_material",
                "set_character_recipe",
                "prepare_library_item",
                "get_job",
                "get_library_item",
                "get_library_materials",
                "register_asset",
                "start_character_test",
                "get_job",
                "get_artifacts",
                "publish_library_item",
            ],
            "environment" =>
            [
                "get_submission_template",
                "get_environment_recipe_template",
                "create_library_item",
                "import_library_material",
                "set_environment_recipe",
                "prepare_library_item",
                "get_job",
                "get_library_item",
                "get_library_materials",
                "register_asset",
                "start_environment_test",
                "get_job",
                "get_artifacts",
                "publish_library_item",
            ],
            "voice" =>
            [
                "get_submission_template",
                "create_library_item",
                "import_library_material",
                "prepare_library_item",
                "get_job",
                "get_library_item",
                "get_library_materials",
                "start_voice_clone",
                "get_job",
                "get_voice_profile",
                "configure_voice_profile",
                "refresh_voice_verification",
                "start_voice_preview",
                "get_job",
                "get_artifacts",
                "review_voice_profile",
                "bind_character_voice",
                "get_character_voice",
            ],
            "production" =>
            [
                "studio_doctor",
                "get_catalog",
                "get_character_voice",
                "get_episode_template",
                "start_sound_effect",
                "create_episode",
                "list_projects",
                "create_project",
                "validate_project",
                "start_studio_preview",
                "get_job",
                "open_studio_preview",
                "update_episode",
                "start_render",
                "get_job",
                "get_artifacts",
                "update_project",
            ],
            _ => throw new ToonException(
                "VALIDATION_ERROR",
                "Choose character, environment, voice or production",
                "/kind"
            ),
        };

    public static JsonNode Get(string kind)
    {
        var tools = Tools(kind);
        var requirements = kind switch
        {
            "character" => new[]
            {
                "Original front/three-quarter materials and confirmed rights",
                "Recipe mapped to supplied pixels; unresolved anatomy remains missing_details",
                "Verified character test MP4 using a real speech recording",
                "Operator visual/listening review before publication",
            },
            "environment" => new[]
            {
                "Original wide view and confirmed rights",
                "Recipe with masks, floor, character slots and cameras; unresolved scenery remains missing_details",
                "Verified environment test MP4 using a real speech recording",
                "Operator review of both close-ups, placement and occlusion before publication",
            },
            "voice" => new[]
            {
                "Original speech sample with rights, cloning_consent and cloud_upload_consent",
                "Technical analysis passed and original listened to by operator",
                "Explicit server clone mode/budget and per-request cloud authorization",
                "Provider verification completed if required, verified WAV preview and operator listening approval",
                "Published render-compatible character version for binding",
            },
            _ => new[]
            {
                "Published character/environment versions and immutable asset references",
                "Approved voice binding for voice_alias=character or configured legacy alias; recordings also supported",
                "Explicit TTS authorization and character cap only for uncached synthesis",
                "Verified final MP4 plus operator visual/listening review",
            },
        };
        return J.Node(
            new
            {
                workflow_version = "1.0.0",
                kind,
                tools,
                requirements,
                guide_resource = "toon://guides/content-workflow",
                workflow_resource = "toon://workflows/" + kind,
                long_operations = tools
                    .Where(tool =>
                        tool.StartsWith("start_", StringComparison.Ordinal) || tool == "prepare_library_item"
                    )
                    .Distinct()
                    .ToArray(),
                execution = "Long operations return a job immediately; run Toon.Worker independently and poll get_job. Closing MCP does not cancel jobs.",
                review = "succeeded means technical completion, not human approval. Inspect get_artifacts and original materials before publication or voice approval.",
                revisions = "Use the returned revision after every mutation. Publish creates immutable semver releases; projects pin exact content and voice revisions.",
                retry = "Same idempotency key and arguments return the same job. A changed request needs a new key. After WORKER_INTERRUPTED read latest state and retry explicitly; never repeat uncertain paid requests.",
                cancellation = "cancel_job stops local work. A dispatched cloud request may still complete; reconcile_voice_clone requires a matching description operation marker or legacy provider label.",
                conditional_steps = kind == "voice"
                    ? new[]
                    {
                        "refresh_voice_verification only when verification_required; it performs metadata GET",
                        "configure_voice_profile changes local delivery settings and requires a new preview and approval; no clone request",
                        "publish_library_item is optional for voice source storage and does not clone or bind a voice",
                    }
                : kind == "production"
                    ? new[]
                    {
                        "start_sound_effect only when requested effects are missing from sound_effects catalog; new SFX requires separate permission and seconds budget",
                    }
                : Array.Empty<string>(),
                get_workflow_makes_cloud_requests = false,
            }
        );
    }
}
