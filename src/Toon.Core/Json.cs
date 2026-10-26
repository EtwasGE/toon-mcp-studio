using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Toon.Core;

public sealed class ToonException(string code, string message, string pointer = "", bool retryable = false)
    : Exception(message)
{
    public string Code { get; } = code;
    public string Pointer { get; } = pointer;
    public bool Retryable { get; } = retryable;
}

public static class J
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, Options)!;

    public static string Str(this JsonNode n, string key) => n[key]!.GetValue<string>();

    public static int Int(this JsonNode n, string key, int fallback = 0) => n[key]?.GetValue<int>() ?? fallback;

    public static double Num(this JsonNode n, string key, double fallback = 0) =>
        n[key]?.GetValue<double>() ?? fallback;

    public static bool Bool(this JsonNode n, string key, bool fallback = false) => n[key]?.GetValue<bool>() ?? fallback;

    public static IEnumerable<JsonNode> Items(this JsonNode n, string key) => n[key]?.AsArray().Select(x => x!) ?? [];

    public static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    public static void Write(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString(Options));
    }

    public static string Canonical(JsonNode n) =>
        n switch
        {
            JsonObject o => "{"
                + string.Join(
                    ",",
                    o.OrderBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x =>
                            JsonSerializer.Serialize(x.Key) + ":" + (x.Value is null ? "null" : Canonical(x.Value))
                        )
                )
                + "}",
            JsonArray a => "[" + string.Join(",", a.Select(x => x is null ? "null" : Canonical(x))) + "]",
            _ => n.ToJsonString(),
        };

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string FileHash(string path)
    {
        using var f = File.OpenRead(path);

        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    public static string Normalize(string text) =>
        Regex.Replace(text.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ");

    // Punctuation remains part of its whitespace token, as required by contract 1.0.0.
    public static string[] Words(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static int Frame(double milliseconds) =>
        (int)Math.Round(milliseconds * 30 / 1000, MidpointRounding.AwayFromZero);

    public static void Require(
        [System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition,
        string code,
        string message,
        string pointer = ""
    )
    {
        if (!condition)
            throw new ToonException(code, message, pointer);
    }
}

public record Issue(string Code, string Message, string Pointer);

public record AudioInfo(string Path, string Hash, long Samples, JsonNode? Alignment, double[] Envelope)
{
    public double DurationMs => Samples / 48.0;
}
