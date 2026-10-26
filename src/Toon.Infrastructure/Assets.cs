using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed class Assets(Config config, Store store)
{
    public string Allowed(string input)
    {
        J.Require(Path.IsPathFullyQualified(input), "VALIDATION_ERROR", "Import path must be absolute");
        var path = Path.GetFullPath(input);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        J.Require(
            config.ImportRoots.Any(r =>
                path.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, comparison)
            ),
            "VALIDATION_ERROR",
            "Path is outside TOON_IMPORT_ROOTS"
        );
        J.Require(File.Exists(path), "ASSET_MISSING", "Import file does not exist");
        var part = new FileInfo(path) as FileSystemInfo;
        while (part != null)
        {
            J.Require(
                (part.Attributes & FileAttributes.ReparsePoint) == 0 && part.LinkTarget == null,
                "VALIDATION_ERROR",
                "Symlink/reparse imports are not permitted"
            );
            part = part is FileInfo file ? file.Directory : ((DirectoryInfo)part).Parent;
        }

        J.Require(File.Exists(path), "ASSET_MISSING", "Import file does not exist");

        return path;
    }

    public string Resolve(JsonNode reference)
    {
        var a = store.Asset(reference);
        var path = Path.Combine(config.Data, a.Str("relative_path"));
        J.Require(
            File.Exists(path) && J.FileHash(path) == a.Str("sha256"),
            "ASSET_MISSING",
            "Immutable asset missing or hash changed"
        );

        return path;
    }

    public async Task<JsonNode> Register(string sourcePath, string kind, string? alignmentPath, CancellationToken ct)
    {
        J.Require(kind is "image" or "audio", "VALIDATION_ERROR", "kind must be image or audio");
        var source = Allowed(sourcePath);
        var ext = Path.GetExtension(source).ToLowerInvariant();
        J.Require(
            kind == "image" ? new[] { ".png", ".jpg", ".jpeg" }.Contains(ext) : new[] { ".wav", ".mp3" }.Contains(ext),
            "VALIDATION_ERROR",
            "Unsupported media extension"
        );
        J.Require(
            new FileInfo(source).Length > 0 && new FileInfo(source).Length <= config.MaxMediaBytes,
            "VALIDATION_ERROR",
            "Media exceeds TOON_MAX_MEDIA_BYTES or is empty"
        );
        byte[] signature = new byte[12];
        using (var stream = File.OpenRead(source))
        {
            _ = stream.Read(signature);
        }
        J.Require(
            kind == "image"
                ? (
                    ext == ".png"
                        ? signature.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                        : signature[0] == 255 && signature[1] == 216
                )
                : (
                    ext == ".wav"
                        ? System.Text.Encoding.ASCII.GetString(signature, 0, 4) == "RIFF"
                            && System.Text.Encoding.ASCII.GetString(signature, 8, 4) == "WAVE"
                        : System.Text.Encoding.ASCII.GetString(signature, 0, 3) == "ID3"
                            || signature[0] == 255 && (signature[1] & 224) == 224
                ),
            "VALIDATION_ERROR",
            "Media magic does not match extension"
        );
        var hash = J.FileHash(source);
        var relative = "assets/" + hash + ext;
        var destination = Path.Combine(config.Data, relative);
        if (!File.Exists(destination))
        {
            var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary);
                J.Require(J.FileHash(temporary) == hash, "VALIDATION_ERROR", "Import changed while copying");
                try
                {
                    File.Move(temporary, destination);
                }
                catch (IOException) when (File.Exists(destination) && J.FileHash(destination) == hash) { }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        J.Require(J.FileHash(destination) == hash, "ASSET_MISSING", "Existing immutable asset has changed");
        var media = new Media(config);
        var probe = await media.Probe(destination, ct);
        JsonNode? alignment = null;
        JsonNode metadata;
        if (kind == "audio")
        {
            var prepared = await media.Prepare(destination, null, ct);
            if (alignmentPath != null)
            {
                var sidecar = Allowed(alignmentPath);
                J.Require(
                    new FileInfo(sidecar).Length <= config.MaxAlignmentBytes,
                    "VALIDATION_ERROR",
                    "Alignment exceeds TOON_MAX_ALIGNMENT_BYTES"
                );
                if (Path.GetExtension(sidecar).Equals(".srt", StringComparison.OrdinalIgnoreCase))
                {
                    alignment = ReadSrt(File.ReadAllText(sidecar), prepared.DurationMs);
                }
                else
                {
                    alignment = J.Read(sidecar);
                    Validation.Schema(alignment, Path.Combine(config.Root, "contracts/audio-alignment.schema.json"));
                    Validation.Alignment(alignment, prepared.DurationMs);
                }
            }

            metadata = J.Node(
                new
                {
                    duration_ms = prepared.DurationMs,
                    samples = prepared.Samples,
                    sample_rate = 48000,
                    channels = 1,
                    original_streams = probe["streams"]!.DeepClone(),
                }
            );
        }
        else
        {
            J.Require(alignmentPath == null, "VALIDATION_ERROR", "Alignment only applies to audio");
            var stream = probe.Items("streams").First();
            J.Require(
                stream.Int("width") * (long)stream.Int("height") <= config.MaxImagePixels
                    && stream.Int("width") > 0
                    && stream.Int("height") > 0,
                "VALIDATION_ERROR",
                "Image exceeds TOON_MAX_IMAGE_PIXELS"
            );
            await Processes.Run(
                config.Ffmpeg,
                ["-v", "error", "-xerror", "-i", destination, "-frames:v", "1", "-f", "null", "-"],
                config.Root,
                ct
            );
            metadata = J.Node(new { width = stream.Int("width"), height = stream.Int("height") });
        }

        var identity = J.Hash(hash + (alignment == null ? "" : J.Canonical(alignment)));
        var asset = J.Node(
            new
            {
                asset_id = "asset_" + identity[..24],
                version = "1.0.0",
                kind,
                mime = kind == "audio"
                    ? ext == ".wav"
                        ? "audio/wav"
                        : "audio/mpeg"
                    : ext == ".png"
                        ? "image/png"
                        : "image/jpeg",
                sha256 = hash,
                bytes = new FileInfo(destination).Length,
                relative_path = relative,
                metadata,
                alignment,
                alignment_sha256 = alignment == null ? null : J.Hash(J.Canonical(alignment)),
                provenance = new
                {
                    source = "operator_local_import",
                    original_filename = Path.GetFileName(source),
                    imported_at = Store.Now,
                    rights = "operator is responsible for usage rights",
                },
            }
        );
        store.PutAsset(asset);

        return store.Asset(J.Node(new { asset_id = asset.Str("asset_id"), version = asset.Str("version") }));
    }

    private static JsonNode ReadSrt(string srt, double duration)
    {
        var phrases = new JsonArray();
        double last = 0;
        foreach (var block in Regex.Split(srt.Trim(), @"\r?\n\s*\r?\n"))
        {
            var match = Regex.Match(
                block,
                @"^\d+\r?\n(\d{2}:\d{2}:\d{2},\d{3}) --> (\d{2}:\d{2}:\d{2},\d{3})\r?\n([\s\S]+)$"
            );
            J.Require(match.Success, "VALIDATION_ERROR", "Invalid SRT block");
            var from = TimeSpan.ParseExact(match.Groups[1].Value, @"hh\:mm\:ss\,fff", null).TotalMilliseconds;
            var to = TimeSpan.ParseExact(match.Groups[2].Value, @"hh\:mm\:ss\,fff", null).TotalMilliseconds;
            J.Require(
                from >= last && to > from && to <= duration,
                "VALIDATION_ERROR",
                "SRT interval is outside audio or overlapping"
            );
            phrases.Add(
                J.Node(
                    new
                    {
                        text = match.Groups[3].Value.Replace("\r", "").Replace("\n", " "),
                        start_ms = from,
                        end_ms = to,
                    }
                )
            );
            last = to;
        }

        return J.Node(
            new
            {
                kind = "phrase_alignment",
                source = "operator_srt",
                phrases,
            }
        );
    }
}
