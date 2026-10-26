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
                "PNG/JPEG: общий вид без персонажей; рекомендуемый вертикальный холст 2160×3840. Приложите схему размещения героев и перекрытий. Плоский эскиз допустим; разбиение на слои и настройка камеры выполняются на следующем этапе.",
            _ =>
                "WAV/MP3: один говорящий без музыки, предпочтительно исходный WAV. Проверяются декодируемость и длительность; качество клонирования и совпадение говорящего не подтверждаются. Образец остаётся локально: клонирование и облачная отправка здесь не выполняются. Значения согласия заполняет оператор.",
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
                workflow = new[]
                {
                    "create_library_item",
                    "import_library_material",
                    "prepare_library_item",
                    "get_job",
                    "get_library_item",
                    "publish_library_item",
                },
                schema_resource = "toon://library/submission-schema",
                template_resource = $"toon://library/templates/{kind}/1.0.0",
                render_supported = false,
                cloud_requests = false,
            }
        );
    }
}
