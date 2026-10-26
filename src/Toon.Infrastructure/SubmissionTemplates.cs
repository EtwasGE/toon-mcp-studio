using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public static class SubmissionTemplates
{
    public static readonly string[] Kinds = ["character", "environment", "voice"];

    public static string[] Roles(string kind) =>
        kind switch
        {
            "character" => ["front", "three_quarter", "expressions", "hands", "mouths", "pose_reference"],
            "environment" => ["wide", "layout", "foreground", "detail"],
            "voice" => ["sample"],
            _ => throw new ToonException("VALIDATION_ERROR", "Unknown library kind", "/kind"),
        };

    public static string[] RequiredRoles(string kind) =>
        kind switch
        {
            "character" => ["front", "three_quarter"],
            "environment" => ["wide"],
            "voice" => ["sample"],
            _ => throw new ToonException("VALIDATION_ERROR", "Unknown library kind", "/kind"),
        };

    public static JsonNode Get(Config config, string kind)
    {
        var roles = Roles(kind);
        var submission = J.Read(Path.Combine(config.Root, "templates/library", kind + ".json"));
        var instructions = kind switch
        {
            "character" =>
                "PNG/JPEG: спереди и в три четверти, одинаковые пропорции; рекомендуемый размер одного вида 2048×2048. Эмоции, руки и рот можно прислать отдельными листами. Слои не обязательны: агент/художник размечает части через get_character_recipe_template и set_character_recipe. Worker собирает риг; неизвестные части перечисляются в missing_details. До публикации рига нужен start_character_test и просмотр. Автоматическая дорисовка не выполняется.",
            "environment" =>
                "PNG/JPEG: общий вид без персонажей; рекомендуемый вертикальный холст 2160×3840. Приложите схему размещения героев и перекрытий. Плоский эскиз допустим: агент/художник размечает слои, маски переднего плана, позиции героев и камеры через get_environment_recipe_template и set_environment_recipe. Worker проверяет координаты по оригиналу. Недостающие части перечисляются в missing_details; скрытый фон автоматически не восстанавливается. До публикации нужен start_environment_test и просмотр общего и обоих крупных планов.",
            _ =>
                "WAV/MP3: один говорящий без музыки, предпочтительно исходный WAV, 60–120 секунд чистой речи; технический минимум 10 секунд. prepare_library_item измеряет длительность, уровень, долю тихих окон и перегрузку, сохраняя оригинал. Это не распознавание речи и не проверка личности: перед start_voice_clone нужно прослушать образец и подтвердить права, согласие говорящего и загрузку в ElevenLabs. Клон создаётся отдельным заданием только с явным разрешением и лимитом. Затем start_voice_preview, прослушивание, review_voice_profile и bind_character_voice. Значения согласия заполняет оператор.",
        };

        return J.Node(
            new
            {
                template_version = "1.0.0",
                kind,
                submission,
                materials = roles.Select(role => new
                {
                    role,
                    required = RequiredRoles(kind).Contains(role),
                    media_kind = kind == "voice" ? "audio" : "image",
                    one_file_per_role = true,
                }),
                limits = new
                {
                    max_media_bytes = config.MaxMediaBytes,
                    max_image_pixels = config.MaxImagePixels,
                    max_audio_seconds = config.MaxAudioSeconds,
                },
                instructions,
                workflow = ContentWorkflows.Tools(kind),
                workflow_resource = "toon://workflows/" + kind,
                guide_resource = "toon://guides/content-workflow",
                schema_resource = "toon://library/submission-schema",
                template_resource = $"toon://library/templates/{kind}/1.0.0",
                render_supported = false,
                cloud_requests = false,
            }
        );
    }
}
