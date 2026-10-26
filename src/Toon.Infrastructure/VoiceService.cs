using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed partial class VoiceService
{
    private readonly Studio studio;
    private readonly Config config;
    private readonly Store store;
    private readonly HttpClient client;
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

    public VoiceService(Studio studio, HttpClient? client = null)
    {
        this.studio = studio;
        config = studio.Config;
        store = studio.Store;
        this.client = client ?? SharedClient;
        using var db = store.Open();
        using var migration = Store.Command(
            db,
            null,
            """
            CREATE TABLE IF NOT EXISTS voice_profiles(id TEXT NOT NULL, revision INTEGER NOT NULL, sha256 TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(id,revision));
            CREATE TABLE IF NOT EXISTS character_voices(character_id TEXT NOT NULL, version TEXT NOT NULL, revision INTEGER NOT NULL, sha256 TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(character_id,version,revision));
            INSERT OR IGNORE INTO migrations VALUES(3);
            """
        );
        migration.ExecuteNonQuery();
    }

    public JsonNode Profile(string id, int? revision = null)
    {
        using var db = store.Open();
        using var query = Store.Command(
            db,
            null,
            "SELECT json,sha256 FROM voice_profiles WHERE id=$id AND ($r IS NULL OR revision=$r) ORDER BY revision DESC LIMIT 1",
            ("$id", id),
            ("$r", revision)
        );
        using var reader = query.ExecuteReader();
        J.Require(reader.Read(), "VOICE_NOT_CONFIGURED", "Voice profile revision does not exist");
        var profile = JsonNode.Parse(reader.GetString(0))!;
        J.Require(
            J.Hash(J.Canonical(profile)) == reader.GetString(1),
            "VOICE_STORAGE_ERROR",
            "Voice profile integrity check failed"
        );
        return profile;
    }

    public JsonArray Profiles()
    {
        using var db = store.Open();
        using var query = Store.Command(
            db,
            null,
            "SELECT id,MAX(revision) FROM voice_profiles GROUP BY id ORDER BY id LIMIT 200"
        );
        using var reader = query.ExecuteReader();
        var references = new List<(string Id, int Revision)>();
        while (reader.Read())
        {
            references.Add((reader.GetString(0), reader.GetInt32(1)));
        }
        reader.Close();
        return new JsonArray(references.Select(r => Profile(r.Id, r.Revision)).ToArray());
    }

    private JsonNode SaveProfile(JsonNode profile, int expected)
    {
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var query = Store.Command(
            db,
            tx,
            "SELECT COALESCE(MAX(revision),0) FROM voice_profiles WHERE id=$id",
            ("$id", profile.Str("profile_id"))
        );
        J.Require(
            Convert.ToInt32(query.ExecuteScalar()) == expected,
            "REVISION_CONFLICT",
            "Read the latest voice profile revision"
        );
        var copy = profile.DeepClone();
        copy["revision"] = expected + 1;
        copy["updated_at"] = Store.Now;
        using var insert = Store.Command(
            db,
            tx,
            "INSERT INTO voice_profiles VALUES($id,$r,$hash,$json)",
            ("$id", copy.Str("profile_id")),
            ("$r", copy.Int("revision")),
            ("$hash", J.Hash(J.Canonical(copy))),
            ("$json", copy.ToJsonString())
        );
        insert.ExecuteNonQuery();
        tx.Commit();
        return copy;
    }

    private void Consent(JsonNode profile)
    {
        var current = studio.Library.Get(profile.Str("source_item_id"));
        RequireConsent(current);
    }

    private static void RequireConsent(JsonNode item)
    {
        var submission = item["submission"]!;
        J.Require(
            submission.Str("kind") == "voice"
                && submission.Bool("rights_confirmed")
                && submission["details"]!.Bool("cloning_consent")
                && submission["details"]!.Bool("cloud_upload_consent"),
            "VOICE_CONSENT_REQUIRED",
            "Confirm source rights, speaker cloning consent and cloud upload consent in the voice submission"
        );
        J.Require(item.Str("state") != "archived", "VOICE_NOT_READY", "Voice source is archived");
    }

    private static JsonNode Descriptor(JsonNode profile) =>
        J.Node(
            new
            {
                provider = "elevenlabs",
                voice_id = profile.Str("voice_id"),
                model_id = "eleven_multilingual_v2",
                voice_settings = new
                {
                    stability = .5,
                    similarity_boost = .75,
                    style = 0,
                    use_speaker_boost = true,
                },
                pronunciation_dictionary_locators = new JsonArray(),
                dictionary_version = "none",
                delivery_applied = "neutral",
                output_format = "mp3_44100_128",
                adapter_version = "1.0.0",
                normalizer_version = "1.0.0",
                source_profile_id = profile.Str("profile_id"),
                source_sample_sha256 = profile.Str("source_sample_sha256"),
            }
        );

    public JsonNode Resolve(JsonNode? reference)
    {
        J.Require(reference != null, "VOICE_NOT_CONFIGURED", "Character has no pinned voice profile");
        var profile = Profile(reference.Str("profile_id"), reference.Int("revision"));
        J.Require(
            profile.Str("state") == "approved" && Profile(profile.Str("profile_id")).Str("state") == "approved",
            "VOICE_NOT_READY",
            "Voice must have an approved listening review and remain enabled"
        );
        Consent(profile);
        return Descriptor(profile);
    }

    public JsonNode? Binding(string characterId, string version)
    {
        using var db = store.Open();
        using var query = Store.Command(
            db,
            null,
            "SELECT json,sha256 FROM character_voices WHERE character_id=$id AND version=$v ORDER BY revision DESC LIMIT 1",
            ("$id", characterId),
            ("$v", version)
        );
        using var reader = query.ExecuteReader();
        if (!reader.Read())
            return null;
        var result = JsonNode.Parse(reader.GetString(0))!;
        J.Require(
            J.Hash(J.Canonical(result)) == reader.GetString(1),
            "VOICE_STORAGE_ERROR",
            "Character voice binding integrity check failed"
        );
        return result;
    }

    public JsonNode Bind(string characterId, string version, int expected, string profileId, int profileRevision)
    {
        J.Require(expected >= 0, "VALIDATION_ERROR", "Expected binding revision must be nonnegative");
        _ = studio.Library.ResolveCharacters(
            J.Node(new { characters = new[] { new { character_id = characterId, version } } })
        );
        var reference = J.Node(new { profile_id = profileId, revision = profileRevision });
        _ = Resolve(reference);
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var query = Store.Command(
            db,
            tx,
            "SELECT COALESCE(MAX(revision),0) FROM character_voices WHERE character_id=$id AND version=$v",
            ("$id", characterId),
            ("$v", version)
        );
        J.Require(
            Convert.ToInt32(query.ExecuteScalar()) == expected,
            "REVISION_CONFLICT",
            "Read current character voice binding"
        );
        var binding = J.Node(
            new
            {
                character_id = characterId,
                version,
                revision = expected + 1,
                voice_profile = reference,
                created_at = Store.Now,
            }
        );
        using var insert = Store.Command(
            db,
            tx,
            "INSERT INTO character_voices VALUES($id,$v,$r,$hash,$json)",
            ("$id", characterId),
            ("$v", version),
            ("$r", expected + 1),
            ("$hash", J.Hash(J.Canonical(binding))),
            ("$json", binding.ToJsonString())
        );
        insert.ExecuteNonQuery();
        tx.Commit();
        return binding;
    }

    public JsonNode Pin(JsonNode input)
    {
        Validation.Schema(input, Path.Combine(config.Root, "contracts/project.schema.json"));
        var project = input.DeepClone();
        var speakers = project
            .Items("scenes")
            .SelectMany(s => s.Items("utterances"))
            .Where(u => u["audio"]!.Str("mode") == "tts" && u["audio"]!.Str("voice_alias") == "character")
            .Select(u => u.Str("speaker_character_id"))
            .ToHashSet();
        foreach (var character in project.Items("characters").Where(c => speakers.Contains(c.Str("character_id"))))
        {
            if (character["voice_profile"] == null)
            {
                var binding = Binding(character.Str("character_id"), character.Str("version"));
                J.Require(binding != null, "VOICE_NOT_CONFIGURED", "Bind a voice to this character version first");
                character["voice_profile"] = binding["voice_profile"]!.DeepClone();
            }
            _ = Resolve(character["voice_profile"]);
        }
        return project;
    }

    public JsonNode Review(string id, int expected, bool approved, string note)
    {
        J.Require(
            note.Trim().Length is > 0 and <= 1000,
            "VALIDATION_ERROR",
            "Provide a listening review note (1–1000 characters)"
        );
        var profile = Profile(id);
        J.Require(profile.Int("revision") == expected, "REVISION_CONFLICT", "Read latest profile revision");
        if (approved)
        {
            Consent(profile);
            J.Require(
                !profile.Bool("requires_verification"),
                "VOICE_VERIFICATION_REQUIRED",
                "Complete verification in ElevenLabs and refresh status"
            );
            using var db = store.Open();
            using var query = Store.Command(
                db,
                null,
                "SELECT json FROM jobs WHERE state='succeeded' AND json_extract(json,'$.job_kind')='voice_preview' AND json_extract(json,'$.snapshot.profile.profile_id')=$id AND json_extract(json,'$.snapshot.profile.revision')=$r",
                ("$id", id),
                ("$r", expected)
            );
            using var reader = query.ExecuteReader();
            var valid = false;
            while (reader.Read())
            {
                var preview = JsonNode
                    .Parse(reader.GetString(0))!
                    .Items("artifacts")
                    .FirstOrDefault(a => a.Str("name") == "voice-preview.wav");
                if (
                    preview != null
                    && File.Exists(preview.Str("local_path"))
                    && J.FileHash(preview.Str("local_path")) == preview.Str("sha256")
                )
                {
                    valid = true;
                    break;
                }
            }
            J.Require(
                valid,
                "VOICE_PREVIEW_REQUIRED",
                "Generate and listen to a verified preview for this profile revision before approval"
            );
        }
        profile["state"] = approved ? "approved" : "rejected";
        profile["review_note"] = note.Trim();
        return SaveProfile(profile, expected);
    }

    private static void VoiceId(string value) =>
        J.Require(Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,128}$"), "PROVIDER_ERROR", "Invalid provider voice ID");

    private static string Secret()
    {
        var key = Secrets.ElevenLabsApiKey();
        J.Require(!string.IsNullOrWhiteSpace(key), "VOICE_NOT_CONFIGURED", "ELEVENLABS_API_KEY is not configured");
        return key;
    }
}
