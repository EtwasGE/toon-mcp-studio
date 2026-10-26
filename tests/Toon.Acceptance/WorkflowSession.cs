using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Toon.Core;
using Toon.Infrastructure;

namespace Toon.Acceptance;

internal sealed class WorkflowSession(Config config) : IAsyncDisposable
{
    private McpClient? client;
    private Process? worker;
    private Task<string>? workerOutput;
    private Task<string>? workerError;
    private int workerNumber;
    public JsonArray Checks { get; } = [];
    public HashSet<string> CalledTools { get; } = [];

    public void Check(bool value, string name)
    {
        J.Require(value, "TEST_FAILED", name);
        Checks.Add(J.Node(new { name, status = "pass" }));
        Console.WriteLine("PASS workflow " + name);
    }

    public async Task<McpClient> Connect()
    {
        if (client != null)
            return client;

        client = await McpClient.CreateAsync(
            new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = "Content workflow acceptance",
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
        return client;
    }

    public async Task<JsonNode> Call(string name, object args, string? error = null)
    {
        CalledTools.Add(name);
        var result = await (await Connect()).CallToolAsync(
            name,
            J.Node(args).AsObject().ToDictionary(p => p.Key, p => (object?)p.Value)
        );
        var body = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
        if (error != null)
        {
            Check(result.IsError == true && body["error"]!.Str("code") == error, "MCP error " + error);
            return body["error"]!;
        }

        J.Require(result.IsError != true, "TEST_FAILED", "MCP " + name + ": " + body["error"]?.ToJsonString());
        return body["data"]!;
    }

    public async Task<string> Resource(string uri)
    {
        var result = await (await Connect()).ReadResourceAsync(uri);
        return result.Contents.OfType<TextResourceContents>().Single().Text;
    }

    public async Task<JsonNode> Wait(string id, string expected = "succeeded", string? stage = null)
    {
        var watch = Stopwatch.StartNew();
        string? previous = null;
        while (watch.Elapsed < TimeSpan.FromMinutes(10))
        {
            var job = await Call("get_job", new { job_id = id });
            var status = job.Str("state") + "/" + job["stage"];
            if (status != previous)
            {
                Console.WriteLine(id + " " + status);
                previous = status;
            }

            if (job.Str("state") == expected && (stage == null || job["stage"]?.ToString() == stage))
                return job;

            if (job.Str("state") is "failed" or "cancelled" or "succeeded")
                throw new Exception("Unexpected terminal job: " + job.ToJsonString());

            await Task.Delay(500);
        }
        throw new TimeoutException("Workflow job timeout: " + id);
    }

    public void StartWorker()
    {
        J.Require(worker == null, "TEST_FAILED", "Stop previous test worker first");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = config.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(config.Root, "src/Toon.Worker/bin/Debug/net10.0/Toon.Worker.dll"));
        start.Environment.Remove("ELEVENLABS_API_KEY");
        start.Environment["TOON_USE_USER_SECRETS"] = "0";
        start.Environment["TOON_ALLOW_VOICE_CLONING"] = "0";
        worker = Process.Start(start)!;
        workerOutput = worker.StandardOutput.ReadToEndAsync();
        workerError = worker.StandardError.ReadToEndAsync();
        workerNumber++;
    }

    public async Task StopWorker()
    {
        if (worker == null)
            return;

        if (!worker.HasExited)
        {
            worker.Kill(true);
        }
        await worker.WaitForExitAsync();
        await File.WriteAllTextAsync(
            Path.Combine(config.Data, $"workflow-worker-{workerNumber}.log"),
            await workerOutput! + await workerError!
        );
        worker.Dispose();
        worker = null;
    }

    public async Task Disconnect()
    {
        if (client == null)
            return;

        await client.DisposeAsync();
        client = null;
    }

    public async ValueTask DisposeAsync()
    {
        await Disconnect();
        await StopWorker();
    }
}
