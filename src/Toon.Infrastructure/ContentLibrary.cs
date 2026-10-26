using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Toon.Core;

namespace Toon.Infrastructure;

// Revisions are immutable documents; heads and release labels are transactional pointers.
public sealed partial class ContentLibrary
{
    private readonly Config config;
    private readonly Store store;
    private readonly Assets assets;

    public ContentLibrary(Config config, Store store, Assets assets)
    {
        this.config = config;
        this.store = store;
        this.assets = assets;
        Directory.CreateDirectory(Path.Combine(config.Data, "library", "blobs"));
        using var db = store.Open();
        using var tx = db.BeginTransaction();
        using var migration = Store.Command(
            db,
            tx,
            """
            CREATE TABLE IF NOT EXISTS library_heads(id TEXT PRIMARY KEY, kind TEXT NOT NULL, revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS library_revisions(id TEXT NOT NULL, revision INTEGER NOT NULL, sha256 TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(id,revision));
            CREATE TABLE IF NOT EXISTS library_versions(id TEXT NOT NULL, version TEXT NOT NULL, revision INTEGER NOT NULL, PRIMARY KEY(id,version));
            CREATE INDEX IF NOT EXISTS library_kind ON library_heads(kind);
            INSERT OR IGNORE INTO migrations VALUES(2);
            """
        );
        migration.ExecuteNonQuery();
        foreach (var id in Validation.Characters)
        {
            Seed(db, tx, "character", id);
        }
        foreach (var id in Validation.Backgrounds)
        {
            Seed(db, tx, "environment", id);
        }
        tx.Commit();
    }

    private void Seed(SqliteConnection db, SqliteTransaction tx, string kind, string reference)
    {
        var id = "builtin_" + kind + "_" + reference;
        using var exists = Store.Command(db, tx, "SELECT 1 FROM library_heads WHERE id=$id", ("$id", id));
        if (exists.ExecuteScalar() != null)
            return;

        var submission = SubmissionTemplates.Get(config, kind)["submission"]!.DeepClone();
        submission["display_name"] = reference;
        submission["description"] = "Built-in original renderer asset";
        submission["rights_confirmed"] = true;
        var item = NewItem(id, submission, "builtin");
        item["state"] = "published";
        item["published_version"] = "1.0.0";
        item["render_supported"] = true;
        item["render_reference"] = J.Node(
            new
            {
                id = reference,
                version = "1.0.0",
                style_id = "warm_editorial_2d",
            }
        );
        item["builtin_manifest"] = J.Read(Path.Combine(config.Root, "styles/warm_editorial_2d/manifest.json"));
        Save(db, tx, item, 0);
        Release(db, tx, id, "1.0.0", 1);
    }

    private static JsonNode NewItem(string id, JsonNode submission, string source = "operator") =>
        J.Node(
            new
            {
                item_id = id,
                revision = 0,
                schema_version = "1.0.0",
                state = "draft",
                source,
                submission,
                materials = new JsonArray(),
                preparation = (JsonNode?)null,
                preparation_job_id = (string?)null,
                published_version = (string?)null,
                render_supported = false,
                created_at = Store.Now,
                updated_at = Store.Now,
            }
        );

    private void CheckSubmission(JsonNode submission)
    {
        J.Require(
            Encoding.UTF8.GetByteCount(submission.ToJsonString()) <= 32768,
            "VALIDATION_ERROR",
            "Submission exceeds 32 KiB"
        );
        Validation.Schema(submission, Path.Combine(config.Root, "contracts/library-submission.schema.json"));
        J.Require(
            !string.IsNullOrWhiteSpace(submission.Str("display_name")),
            "VALIDATION_ERROR",
            "Display name cannot be blank",
            "/display_name"
        );
    }

    // Flush before committing a reference. Interrupted writes leave only unreferenced files.
    private string WriteBlob(JsonNode document)
    {
        var text = J.Canonical(document);
        var hash = J.Hash(text);
        var path = BlobPath(hash);
        if (!File.Exists(path))
        {
            var temp = path + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(Encoding.UTF8.GetBytes(text));
                    file.Flush(true);
                }
                try
                {
                    File.Move(temp, path);
                }
                catch (IOException) when (File.Exists(path) && J.FileHash(path) == hash) { }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }

        J.Require(J.FileHash(path) == hash, "LIBRARY_STORAGE_ERROR", "Immutable library document has changed");

        return hash;
    }

    private string BlobPath(string hash) => Path.Combine(config.Data, "library", "blobs", hash + ".json");

    private JsonNode Save(SqliteConnection db, SqliteTransaction tx, JsonNode item, int expected)
    {
        item.AsObject().Remove("content_sha256");
        item.AsObject().Remove("manifest_path");
        item["revision"] = expected + 1;
        item["updated_at"] = Store.Now;
        var hash = WriteBlob(item);
        using var head = Store.Command(
            db,
            tx,
            expected == 0
                ? "INSERT INTO library_heads VALUES($id,$kind,$next)"
                : "UPDATE library_heads SET revision=$next WHERE id=$id AND revision=$expected",
            ("$id", item.Str("item_id")),
            ("$kind", item["submission"]!.Str("kind")),
            ("$next", expected + 1),
            ("$expected", expected)
        );
        J.Require(head.ExecuteNonQuery() == 1, "REVISION_CONFLICT", "Library item changed; read its latest revision");
        using var insert = Store.Command(
            db,
            tx,
            "INSERT INTO library_revisions VALUES($id,$r,$hash,$json)",
            ("$id", item.Str("item_id")),
            ("$r", expected + 1),
            ("$hash", hash),
            ("$json", J.Canonical(item))
        );
        insert.ExecuteNonQuery();

        return item;
    }

    private JsonNode Read(SqliteConnection db, SqliteTransaction? tx, string id, int? revision = null)
    {
        using var command = Store.Command(
            db,
            tx,
            "SELECT r.json,r.sha256 FROM library_revisions r JOIN library_heads h ON h.id=r.id WHERE r.id=$id AND r.revision=COALESCE($r,h.revision)",
            ("$id", id),
            ("$r", revision)
        );
        using var reader = command.ExecuteReader();
        J.Require(reader.Read(), "LIBRARY_NOT_FOUND", "Library item or revision does not exist");
        var json = reader.GetString(0);
        var hash = reader.GetString(1);
        J.Require(
            J.Hash(json) == hash && File.Exists(BlobPath(hash)) && J.FileHash(BlobPath(hash)) == hash,
            "LIBRARY_STORAGE_ERROR",
            "Library revision is missing or has changed"
        );
        var result = JsonNode.Parse(json)!;
        result["content_sha256"] = hash;
        result["manifest_path"] = BlobPath(hash);

        return result;
    }

    private JsonNode Editable(
        SqliteConnection db,
        SqliteTransaction tx,
        string id,
        int expected,
        params string[] states
    )
    {
        J.Require(expected > 0, "VALIDATION_ERROR", "expected_revision must be positive");
        var item = Read(db, tx, id);
        J.Require(
            item.Int("revision") == expected,
            "REVISION_CONFLICT",
            "Library item changed; read its latest revision"
        );
        J.Require(item.Str("source") == "operator", "LIBRARY_READ_ONLY", "Built-in library entries cannot be edited");
        J.Require(
            states.Contains(item.Str("state")),
            "LIBRARY_STATE_CONFLICT",
            "Operation is not allowed in the current state"
        );

        return item;
    }

    private static void ResetDraft(JsonNode item)
    {
        item["state"] = "draft";
        item["preparation"] = null;
        item["preparation_job_id"] = null;
        item["published_version"] = null;
        item.AsObject().Remove("review");
        item.AsObject().Remove("last_error");
    }

    public JsonNode Create(JsonNode submission)
    {
        CheckSubmission(submission);
        var id = "content_" + Guid.NewGuid().ToString("N");
        using (var db = store.Open())
        using (var tx = db.BeginTransaction())
        {
            Save(db, tx, NewItem(id, submission.DeepClone()), 0);
            tx.Commit();
        }

        return Get(id);
    }

    public JsonNode Update(string id, int expected, JsonNode submission)
    {
        CheckSubmission(submission);
        Recover();
        using (var db = store.Open())
        using (var tx = db.BeginTransaction())
        {
            var item = Editable(db, tx, id, expected, "draft", "review", "published", "archived");
            J.Require(
                item["submission"]!.Str("kind") == submission.Str("kind"),
                "VALIDATION_ERROR",
                "Library kind cannot change",
                "/kind"
            );
            item["submission"] = submission.DeepClone();
            ResetDraft(item);
            Save(db, tx, item, expected);
            tx.Commit();
        }

        return Get(id);
    }

    public JsonNode Get(string id, int? revision = null, string? version = null)
    {
        J.Require(revision == null || revision > 0, "VALIDATION_ERROR", "revision must be positive");
        J.Require(revision == null || version == null, "VALIDATION_ERROR", "Choose revision or version, not both");
        Recover();
        using var db = store.Open();
        if (version != null)
        {
            using var release = Store.Command(
                db,
                null,
                "SELECT revision FROM library_versions WHERE id=$id AND version=$v",
                ("$id", id),
                ("$v", version)
            );
            var found = release.ExecuteScalar();
            J.Require(found != null, "LIBRARY_VERSION_NOT_FOUND", "Published library version does not exist");
            revision = Convert.ToInt32(found);
        }

        return Read(db, null, id, revision);
    }

    public JsonNode List(string? kind = null, string? state = null, int limit = 50, int offset = 0)
    {
        J.Require(kind == null || SubmissionTemplates.Kinds.Contains(kind), "VALIDATION_ERROR", "Unknown library kind");
        J.Require(
            state == null || new[] { "draft", "preparing", "review", "published", "archived" }.Contains(state),
            "VALIDATION_ERROR",
            "Unknown library state"
        );
        J.Require(
            limit is > 0 and <= 200 && offset >= 0,
            "VALIDATION_ERROR",
            "limit must be 1–200 and offset nonnegative"
        );
        Recover();
        using var db = store.Open();
        using var cmd = Store.Command(
            db,
            null,
            "SELECT h.id FROM library_heads h JOIN library_revisions r ON r.id=h.id AND r.revision=h.revision WHERE ($kind IS NULL OR h.kind=$kind) AND ($state IS NULL OR json_extract(r.json,'$.state')=$state) ORDER BY h.id LIMIT $limit OFFSET $offset",
            ("$kind", kind),
            ("$state", state),
            ("$limit", limit),
            ("$offset", offset)
        );
        using var reader = cmd.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }
        reader.Close();

        return new JsonArray(
            ids.Select(id =>
                {
                    var item = Read(db, null, id);

                    return J.Node(
                        new
                        {
                            item_id = id,
                            revision = item.Int("revision"),
                            kind = item["submission"]!.Str("kind"),
                            display_name = item["submission"]!.Str("display_name"),
                            state = item.Str("state"),
                            source = item.Str("source"),
                            published_version = item["published_version"]?.ToString(),
                            render_supported = item.Bool("render_supported"),
                        }
                    );
                })
                .ToArray()
        );
    }

    public JsonNode Versions(string id)
    {
        using var db = store.Open();
        _ = Read(db, null, id);
        using var cmd = Store.Command(
            db,
            null,
            "SELECT version,revision FROM library_versions WHERE id=$id ORDER BY revision",
            ("$id", id)
        );
        using var reader = cmd.ExecuteReader();
        var result = new JsonArray();
        while (reader.Read())
        {
            result.Add(J.Node(new { version = reader.GetString(0), revision = reader.GetInt32(1) }));
        }
        return result;
    }

    public JsonNode Package(string id, int revision)
    {
        var item = Get(id, revision);
        var preparation = item["preparation"];
        J.Require(preparation != null, "LIBRARY_NOT_READY", "This revision has no prepared package");
        var hash = preparation.Str("package_sha256");
        var path = BlobPath(hash);
        J.Require(
            File.Exists(path) && J.FileHash(path) == hash,
            "LIBRARY_STORAGE_ERROR",
            "Prepared package is missing or changed"
        );

        return J.Read(path);
    }

    public JsonNode Materials(string id, int revision)
    {
        var item = Get(id, revision);

        return new JsonArray(
            item.Items("materials")
                .Select(material =>
                {
                    var asset = store.Asset(material["asset"]!);
                    var path = assets.Resolve(material["asset"]!);
                    J.Require(
                        asset.Str("sha256") == material.Str("sha256"),
                        "VERIFY_FAILED",
                        "Material differs from revision"
                    );

                    return J.Node(
                        new
                        {
                            role = material.Str("role"),
                            name = material.Str("role") + Path.GetExtension(path),
                            asset = material["asset"]!.DeepClone(),
                            mime = asset.Str("mime"),
                            sha256 = asset.Str("sha256"),
                            local_path = path,
                            resource_uri = new Uri(path).AbsoluteUri,
                            metadata = asset["metadata"]!.DeepClone(),
                        }
                    );
                })
                .ToArray()
        );
    }

    public async Task<JsonNode> Import(
        string id,
        int expected,
        string role,
        string sourcePath,
        string note,
        CancellationToken ct
    )
    {
        J.Require(note.Length <= 2000, "VALIDATION_ERROR", "Material note exceeds 2000 characters");
        Recover();
        string kind;
        using (var db = store.Open())
        using (var tx = db.BeginTransaction())
        {
            var item = Editable(db, tx, id, expected, "draft", "review", "published");
            kind = item["submission"]!.Str("kind");
            J.Require(
                SubmissionTemplates.Roles(kind).Contains(role),
                "VALIDATION_ERROR",
                "Role does not belong to this submission kind",
                "/role"
            );
        }
        // Existing importer enforces allowed roots, signatures, limits and decoding. No source path enters a revision.
        var asset = await assets.Register(sourcePath, kind == "voice" ? "audio" : "image", null, ct);
        ct.ThrowIfCancellationRequested();
        using (var db = store.Open())
        using (var tx = db.BeginTransaction())
        {
            var item = Editable(db, tx, id, expected, "draft", "review", "published");
            var materials = item["materials"]!.AsArray();
            var previous = materials.FirstOrDefault(m => m!.Str("role") == role);
            if (previous != null)
            {
                materials.Remove(previous);
            }
            materials.Add(
                J.Node(
                    new
                    {
                        role,
                        asset = new { asset_id = asset.Str("asset_id"), version = asset.Str("version") },
                        sha256 = asset.Str("sha256"),
                        note,
                        source = "operator_import",
                        imported_at = Store.Now,
                    }
                )
            );
            ResetDraft(item);
            Save(db, tx, item, expected);
            tx.Commit();
        }

        return Get(id);
    }

    public JsonNode RemoveMaterial(string id, int expected, string role)
    {
        Recover();
        using (var db = store.Open())
        using (var tx = db.BeginTransaction())
        {
            var item = Editable(db, tx, id, expected, "draft", "review", "published");
            var materials = item["materials"]!.AsArray();
            var previous = materials.FirstOrDefault(m => m!.Str("role") == role);
            J.Require(previous != null, "ASSET_MISSING", "No material in that role");
            materials.Remove(previous);
            ResetDraft(item);
            Save(db, tx, item, expected);
            tx.Commit();
        }

        return Get(id);
    }

    public JsonNode Publish(string id, int expected, string version, bool approved, string note)
    {
        J.Require(
            Regex.IsMatch(version, @"^(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})$"),
            "VALIDATION_ERROR",
            "Version must be a numeric major.minor.patch"
        );
        J.Require(approved, "REVIEW_REQUIRED", "Explicit review approval is required");
        J.Require(
            note.Trim().Length is > 0 and <= 2000,
            "REVIEW_REQUIRED",
            "Provide a review note of 1–2000 characters"
        );
        Recover();
        using (var db = store.Open())
        using (var tx = db.BeginTransaction())
        {
            var item = Editable(db, tx, id, expected, "review");
            var preparation = item["preparation"]!;
            J.Require(
                preparation.Bool("ready_for_publication"),
                "LIBRARY_NOT_READY",
                "Resolve preparation errors and prepare a new revision"
            );
            var packagePath = BlobPath(preparation.Str("package_sha256"));
            J.Require(
                File.Exists(packagePath) && J.FileHash(packagePath) == preparation.Str("package_sha256"),
                "LIBRARY_STORAGE_ERROR",
                "Prepared package is missing or changed"
            );
            foreach (var m in item.Items("materials"))
            {
                _ = assets.Resolve(m["asset"]!);
            }
            using var versions = Store.Command(
                db,
                tx,
                "SELECT version FROM library_versions WHERE id=$id",
                ("$id", id)
            );
            using (var reader = versions.ExecuteReader())
                while (reader.Read())
                {
                    J.Require(
                        Version.Parse(version) > Version.Parse(reader.GetString(0)),
                        "LIBRARY_VERSION_CONFLICT",
                        "Published versions are immutable; choose a newer version"
                    );
                }
            item["state"] = "published";
            item["published_version"] = version;
            item["review"] = J.Node(
                new
                {
                    approved = true,
                    note,
                    reviewed_at = Store.Now,
                }
            );
            Save(db, tx, item, expected);
            Release(db, tx, id, version, item.Int("revision"));
            tx.Commit();
        }

        return Get(id);
    }

    private static void Release(SqliteConnection db, SqliteTransaction tx, string id, string version, int revision)
    {
        using var insert = Store.Command(
            db,
            tx,
            "INSERT INTO library_versions VALUES($id,$version,$r)",
            ("$id", id),
            ("$version", version),
            ("$r", revision)
        );
        insert.ExecuteNonQuery();
    }

    public JsonNode Archive(string id, int expected)
    {
        Recover();
        using (var db = store.Open())
        using (var tx = db.BeginTransaction())
        {
            var item = Editable(db, tx, id, expected, "draft", "review", "published");
            item["state"] = "archived";
            Save(db, tx, item, expected);
            tx.Commit();
        }

        return Get(id);
    }
}
