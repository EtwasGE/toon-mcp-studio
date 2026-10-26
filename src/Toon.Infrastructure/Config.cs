using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed class Config
{
    public string Root { get; } = Path.GetFullPath(Environment.GetEnvironmentVariable("TOON_APP_ROOT") ?? FindRoot());
    public string Data { get; }
    public string[] ImportRoots { get; }
    public string Node => Environment.GetEnvironmentVariable("TOON_NODE") ?? "node";
    public string Ffmpeg =>
        Environment.GetEnvironmentVariable("TOON_FFMPEG")
        ?? Path.Combine(
            Root,
            "renderer/node_modules/ffmpeg-static",
            OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"
        );
    public string Ffprobe =>
        Environment.GetEnvironmentVariable("TOON_FFPROBE")
        ?? Path.Combine(
            Root,
            "renderer/node_modules/ffprobe-static/bin",
            OperatingSystem.IsWindows() ? "win32/x64/ffprobe.exe" : "linux/x64/ffprobe"
        );
    public int LeaseSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("TOON_LEASE_SECONDS"), out var v) ? Math.Clamp(v, 10, 300) : 30;
    public int HeartbeatSeconds => Limit("TOON_HEARTBEAT_SECONDS", 5, Math.Max(1, LeaseSeconds / 3));
    public int MaxMediaBytes => Limit("TOON_MAX_MEDIA_BYTES", 25 * 1024 * 1024, 512 * 1024 * 1024);
    public int MaxAlignmentBytes => Limit("TOON_MAX_ALIGNMENT_BYTES", 1024 * 1024, 8 * 1024 * 1024);
    public int MaxImagePixels => Limit("TOON_MAX_IMAGE_PIXELS", 12000000, 64000000);
    public int MaxAudioSeconds => Limit("TOON_MAX_AUDIO_SECONDS", 120, 300);
    public int TtsLimit =>
        int.TryParse(Environment.GetEnvironmentVariable("TOON_MAX_TTS_CHARACTERS"), out var v)
            ? Math.Clamp(v, 0, 100000)
            : 4000;
    public JsonNode Voices => JsonNode.Parse(Environment.GetEnvironmentVariable("TOON_VOICE_ALIASES") ?? "{}")!;

    public string RendererFingerprint() =>
        J.Hash(
            string.Join(
                "\n",
                Directory
                    .GetFiles(Path.Combine(Root, "renderer/src"), "*", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(Path.Combine(Root, "styles/warm_editorial_2d/characters"), "*.rig.json"))
                    .Concat(
                        new[]
                        {
                            Path.Combine(Root, "renderer/render.mjs"),
                            Path.Combine(Root, "renderer/package-lock.json"),
                            Path.Combine(Root, "styles/warm_editorial_2d/manifest.json"),
                        }
                    )
                    .Order(StringComparer.Ordinal)
                    .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/') + ":" + J.FileHash(path))
            )
        );

    public Config()
    {
        Data = Path.GetFullPath(Environment.GetEnvironmentVariable("TOON_DATA_ROOT") ?? Path.Combine(Root, ".data"));
        ImportRoots = (Environment.GetEnvironmentVariable("TOON_IMPORT_ROOTS") ?? Path.Combine(Root, "examples"))
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFullPath)
            .ToArray();
        foreach (var folder in new[] { "database", "assets", "audio-cache", "projects", "jobs", "artifacts" })
        {
            Directory.CreateDirectory(Path.Combine(Data, folder));
        }
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Toon.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new ToonException("VALIDATION_ERROR", "Set TOON_APP_ROOT to installation directory");
    }

    private static int Limit(string name, int fallback, int max) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, 1, max)
            : Math.Min(fallback, max);
}
