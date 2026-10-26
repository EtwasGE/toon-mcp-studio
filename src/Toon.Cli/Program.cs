using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Toon.Core;
using Toon.Infrastructure;

var config = new Config();
await using var client = await McpClient.CreateAsync(
    new StdioClientTransport(
        new StdioClientTransportOptions
        {
            Name = "Toon CLI",
            Command = "dotnet",
            Arguments = [Path.Combine(config.Root, "src/Toon.Mcp/bin/Debug/net10.0/Toon.Mcp.dll")],
            WorkingDirectory = config.Root,
        }
    ),
    new McpClientOptions
    {
        ProtocolVersion = "2026-07-28",
        DiscoverProbeTimeout = TimeSpan.FromSeconds(60),
        InitializationTimeout = TimeSpan.FromSeconds(90),
    }
);
async Task<JsonNode> Call(string name, object args)
{
    var result = await client.CallToolAsync(
        name,
        J.Node(args).AsObject().ToDictionary(x => x.Key, x => (object?)x.Value)
    );
    var node = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
    if (result.IsError == true)
        throw new ToonException(node["error"]!.Str("code"), node["error"]!.Str("message"));

    return node["data"]!;
}
if (args.FirstOrDefault() == "doctor")
{
    Console.WriteLine((await Call("studio_doctor", new { })).ToJsonString(J.Options));

    return;
}

if (args.FirstOrDefault() == "status")
{
    Console.WriteLine((await Call("get_job", new { job_id = args[1] })).ToJsonString(J.Options));

    return;
}

if (args.FirstOrDefault() == "cancel")
{
    Console.WriteLine((await Call("cancel_job", new { job_id = args[1] })).ToJsonString(J.Options));

    return;
}

if (args.FirstOrDefault() != "demo")
{
    Console.Error.WriteLine("Usage: Toon.Cli doctor | demo [preview|final] [--wait] | status JOB_ID | cancel JOB_ID");
    Environment.ExitCode = 1;

    return;
}
var quality = args.ElementAtOrDefault(1) is "preview" ? "preview" : "final";
var directory = Path.Combine(config.Root, "examples/offline-dialogue");
var project = J.Read(Path.Combine(directory, "project.json"));
foreach (var u in project.Items("scenes").SelectMany(s => s.Items("utterances")))
{
    var asset = await Call(
        "register_asset",
        new { source_path = Path.Combine(directory, u["audio"]!["asset"]!.Str("asset_id") + ".wav"), kind = "audio" }
    );
    u["audio"]!["asset"] = J.Node(new { asset_id = asset.Str("asset_id"), version = asset.Str("version") });
}
var screen = await Call("register_asset", new { source_path = Path.Combine(directory, "screen.png"), kind = "image" });
project["scenes"]![1]!["overlays"]![0]!["content"]!["asset"] = J.Node(
    new { asset_id = screen.Str("asset_id"), version = screen.Str("version") }
);
var saved = await Call("create_project", new { project });
var job = await Call(
    "start_render",
    new
    {
        project_id = saved.Str("project_id"),
        revision = 1,
        quality,
        idempotency_key = "demo-" + Guid.NewGuid().ToString("N"),
        allow_new_tts = false,
        max_new_tts_characters = 0,
    }
);
J.Write(
    Path.Combine(config.Data, "latest-demo.json"),
    J.Node(
        new
        {
            project_id = saved.Str("project_id"),
            revision = 1,
            job_id = job.Str("job_id"),
        }
    )
);
Console.WriteLine($"Project {saved.Str("project_id")} revision 1; job {job.Str("job_id")}");
if (!job.Bool("worker_alive"))
{
    Console.WriteLine("Queued. Start Toon.Worker independently to render.");

    return;
}

if (args.Contains("--wait"))
{
    string previous = "";
    while (job.Str("state") is "queued" or "running" or "cancelling")
    {
        var status = job.Str("state") + " / " + job["stage"];
        if (status != previous)
        {
            Console.WriteLine(status);
            previous = status;
        }

        await Task.Delay(1000);
        job = await Call("get_job", new { job_id = job.Str("job_id") });
    }

    if (job.Str("state") != "succeeded")
    {
        Console.Error.WriteLine(job.ToJsonString(J.Options));
        Environment.ExitCode = 1;
    }
    else
    {
        Console.WriteLine((await Call("get_artifacts", new { job_id = job.Str("job_id") })).ToJsonString(J.Options));
    }
}
