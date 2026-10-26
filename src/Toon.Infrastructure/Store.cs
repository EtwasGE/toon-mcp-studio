using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
using Toon.Core;

namespace Toon.Infrastructure;

public sealed class Store
{
    private readonly Config config;
    public Store(Config config) {
        this.config = config;
        using var db = Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS migrations(version INTEGER PRIMARY KEY);
            INSERT OR IGNORE INTO migrations VALUES(1);
            CREATE TABLE IF NOT EXISTS heads(id TEXT PRIMARY KEY, revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS revisions(id TEXT NOT NULL, revision INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY(id,revision));
            CREATE TABLE IF NOT EXISTS assets(id TEXT NOT NULL, version TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(id,version));
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, idem TEXT UNIQUE NOT NULL, request_hash TEXT NOT NULL, state TEXT NOT NULL, lease_until TEXT, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS worker(id INTEGER PRIMARY KEY CHECK(id=1), heartbeat TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS provider_requests(cache_key TEXT PRIMARY KEY, state TEXT NOT NULL, json TEXT NOT NULL);
            """; cmd.ExecuteNonQuery();
    }
    public SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(config.Data, "database/toon.db"), DefaultTimeout = 10, Pooling = true }.ToString()); c.Open(); return c; }
    public static string Now => DateTimeOffset.UtcNow.ToString("O");
    public static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args) { var c = db.CreateCommand(); c.Transaction = tx; c.CommandText = sql; foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value); return c; }
    public JsonNode SaveProject(JsonNode project, string? id = null, int expected = 0) {
        id ??= "project_" + Guid.NewGuid().ToString("N");
        using var db = Open(); using var tx = db.BeginTransaction();
        using var head = Command(db, tx, "SELECT revision FROM heads WHERE id=$id", ("$id", id)); var current = head.ExecuteScalar();
        if (expected > 0) { J.Require(current != null, "PROJECT_NOT_FOUND", "Project does not exist"); J.Require(Convert.ToInt32(current) == expected, "REVISION_CONFLICT", $"Expected revision {expected}; current revision is {current}"); }
        var revision = (current == null ? 0 : Convert.ToInt32(current)) + 1;
        using var insert = Command(db, tx, "INSERT INTO revisions VALUES($id,$r,$json); INSERT INTO heads VALUES($id,$r) ON CONFLICT(id) DO UPDATE SET revision=$r", ("$id", id), ("$r", revision), ("$json", project.ToJsonString())); insert.ExecuteNonQuery(); tx.Commit();
        J.Write(Path.Combine(config.Data, "projects", id, $"revision-{revision}.json"), project);
        return J.Node(new { project_id = id, revision });
    }
    public JsonNode Project(string id, int? revision = null) {
        using var db = Open(); using var cmd = Command(db, null, "SELECT revision,json FROM revisions WHERE id=$id AND ($r IS NULL OR revision=$r) ORDER BY revision DESC LIMIT 1", ("$id", id), ("$r", revision)); using var reader = cmd.ExecuteReader();
        J.Require(reader.Read(), "PROJECT_NOT_FOUND", "Project revision does not exist"); return J.Node(new { project_id = id, revision = reader.GetInt32(0), project = JsonNode.Parse(reader.GetString(1)) });
    }
    public void PutAsset(JsonNode asset) { using var db = Open(); using var cmd = Command(db, null, "INSERT OR IGNORE INTO assets VALUES($id,$v,$json)", ("$id", asset.Str("asset_id")), ("$v", asset.Str("version")), ("$json", asset.ToJsonString())); cmd.ExecuteNonQuery(); }
    public JsonNode Asset(JsonNode reference) {
        using var db = Open(); using var cmd = Command(db, null, "SELECT json FROM assets WHERE id=$id AND version=$v", ("$id", reference.Str("asset_id")), ("$v", reference.Str("version"))); var json = cmd.ExecuteScalar();
        if (json is null) { using var exists = Command(db, null, "SELECT 1 FROM assets WHERE id=$id", ("$id", reference.Str("asset_id"))); throw new ToonException(exists.ExecuteScalar() is null ? "ASSET_MISSING" : "ASSET_VERSION_MISMATCH", "Asset reference is unavailable"); }
        return JsonNode.Parse((string)json)!;
    }
    public JsonNode Enqueue(JsonNode request, JsonNode snapshot) {
        var hash = J.Hash(J.Canonical(request)); using var db = Open(); using var tx = db.BeginTransaction();
        using var existing = Command(db, tx, "SELECT request_hash,json FROM jobs WHERE idem=$key", ("$key", request.Str("idempotency_key"))); using var reader = existing.ExecuteReader();
        if (reader.Read()) { J.Require(reader.GetString(0) == hash, "IDEMPOTENCY_CONFLICT", "Idempotency key belongs to a different request"); return JsonNode.Parse(reader.GetString(1))!; } reader.Close();
        var job = J.Node(new { job_id = "job_" + Guid.NewGuid().ToString("N"), project_id = request.Str("project_id"), revision = request.Int("revision"), quality = request.Str("quality"), state = "queued", stage = (string?)null, progress = (double?)null, created_at = Now, started_at = (string?)null, ended_at = (string?)null, heartbeat = (string?)null, lease_until = (string?)null, attempt = 0, request = request.DeepClone(), snapshot = snapshot.DeepClone(), snapshot_hash = J.Hash(J.Canonical(snapshot)), artifacts = new JsonArray(), provider_usage_confirmed = new JsonArray(), provider_usage_unknown = new JsonArray(), poll_after_ms = 1000 });
        using var insert = Command(db, tx, "INSERT INTO jobs VALUES($id,$key,$hash,'queued',NULL,$json)", ("$id", job.Str("job_id")), ("$key", request.Str("idempotency_key")), ("$hash", hash), ("$json", job.ToJsonString())); insert.ExecuteNonQuery(); tx.Commit(); return job;
    }
    public JsonNode? Existing(JsonNode request) {
        using var db = Open(); using var cmd = Command(db, null, "SELECT request_hash,json FROM jobs WHERE idem=$key", ("$key", request.Str("idempotency_key"))); using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null;
        J.Require(reader.GetString(0) == J.Hash(J.Canonical(request)), "IDEMPOTENCY_CONFLICT", "Idempotency key belongs to another request"); return JsonNode.Parse(reader.GetString(1))!;
    }
    public JsonNode Job(string id) { using var db = Open(); using var cmd = Command(db, null, "SELECT json FROM jobs WHERE id=$id", ("$id", id)); return JsonNode.Parse((string?)cmd.ExecuteScalar() ?? throw new ToonException("VALIDATION_ERROR", "Unknown job"))!; }
    public JsonNode ChangeJob(string id, Action<JsonNode> action) {
        using var db = Open(); using var tx = db.BeginTransaction(); using var read = Command(db, tx, "SELECT json FROM jobs WHERE id=$id", ("$id", id)); var job = JsonNode.Parse((string?)read.ExecuteScalar() ?? throw new ToonException("VALIDATION_ERROR", "Unknown job"))!;
        action(job); Update(db, tx, job); tx.Commit(); return job;
    }
    private static void Update(SqliteConnection db, SqliteTransaction tx, JsonNode job) { using var cmd = Command(db, tx, "UPDATE jobs SET state=$s,lease_until=$l,json=$j WHERE id=$id", ("$s", job.Str("state")), ("$l", job["lease_until"]?.ToString()), ("$j", job.ToJsonString()), ("$id", job.Str("job_id"))); cmd.ExecuteNonQuery(); }
    public void Recover() {
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = Command(db, tx, "SELECT json FROM jobs WHERE state IN ('running','cancelling') AND lease_until < $now", ("$now", Now)); using var reader = cmd.ExecuteReader(); var jobs = new List<JsonNode>(); while (reader.Read()) jobs.Add(JsonNode.Parse(reader.GetString(0))!); reader.Close();
        foreach (var j in jobs) { var cancelled = j.Str("state") == "cancelling"; j["state"] = cancelled ? "cancelled" : "failed"; j["ended_at"] = Now; j["error"] = J.Node(new { code = cancelled ? "CANCELLED" : "WORKER_INTERRUPTED", message = "Worker lease expired; unpublished files are not artifacts" }); Update(db, tx, j); }
        tx.Commit();
    }
    public JsonNode? Claim() {
        Recover(); using var db = Open(); using var tx = db.BeginTransaction();
        using var active = Command(db, tx, "SELECT COUNT(*) FROM jobs WHERE state IN ('running','cancelling')"); if (Convert.ToInt32(active.ExecuteScalar()) != 0) return null;
        using var cmd = Command(db, tx, "SELECT json FROM jobs WHERE state='queued' ORDER BY rowid LIMIT 1"); if (cmd.ExecuteScalar() is not string json) return null;
        var job = JsonNode.Parse(json)!; job["state"] = "running"; job["stage"] = "preflight"; job["started_at"] = Now; job["heartbeat"] = Now; job["lease_until"] = DateTimeOffset.UtcNow.AddSeconds(config.LeaseSeconds).ToString("O"); job["attempt"] = job.Int("attempt") + 1; Update(db, tx, job); tx.Commit(); return job;
    }
    public void Heartbeat() { using var db = Open(); using var cmd = Command(db, null, "INSERT INTO worker VALUES(1,$n) ON CONFLICT(id) DO UPDATE SET heartbeat=$n", ("$n", Now)); cmd.ExecuteNonQuery(); }
    public bool WorkerAlive() { using var db = Open(); using var cmd = Command(db, null, "SELECT heartbeat FROM worker WHERE id=1"); return cmd.ExecuteScalar() is string n && DateTimeOffset.Parse(n) > DateTimeOffset.UtcNow.AddSeconds(-config.LeaseSeconds); }
    public JsonNode Cancel(string id) => ChangeJob(id, j => { var state = j.Str("state"); if (state == "queued") { j["state"] = "cancelled"; j["ended_at"] = Now; } else if (state == "running") j["state"] = "cancelling"; });
    public void Journal(string key, string state, JsonNode data, bool insert = false) { using var db = Open(); using var cmd = Command(db, null, insert ? "INSERT INTO provider_requests VALUES($k,$s,$j)" : "UPDATE provider_requests SET state=$s,json=$j WHERE cache_key=$k", ("$k", key), ("$s", state), ("$j", data.ToJsonString())); cmd.ExecuteNonQuery(); }
    public JsonNode? Request(string key) { using var db = Open(); using var cmd = Command(db, null, "SELECT json FROM provider_requests WHERE cache_key=$k", ("$k", key)); return cmd.ExecuteScalar() is string json ? JsonNode.Parse(json) : null; }
}
