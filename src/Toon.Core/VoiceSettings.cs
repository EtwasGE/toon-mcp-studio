using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Toon.Core;

public sealed record VoiceSettings
{
    [JsonPropertyName("stability")]
    [Range(0.0, 1.0)]
    [Description("Voice stability from 0 to 1.")]
    public required double Stability { get; init; }

    [JsonPropertyName("similarity_boost")]
    [Range(0.0, 1.0)]
    [Description("Similarity to the source voice from 0 to 1.")]
    public required double SimilarityBoost { get; init; }

    [JsonPropertyName("style")]
    [Range(0.0, 1.0)]
    [Description("Legacy setting, ignored by Eleven V4.")]
    public required double Style { get; init; }

    [JsonPropertyName("use_speaker_boost")]
    [Description("Legacy setting, ignored by Eleven V4.")]
    public required bool UseSpeakerBoost { get; init; }

    [JsonPropertyName("speed")]
    [Range(0.7, 1.2)]
    [Description("Legacy setting, ignored by Eleven V4; use 1 for compatibility.")]
    public required double Speed { get; init; }
}
