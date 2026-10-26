using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Toon.Core;
using Toon.Infrastructure;

var config=new Config();var studio=new Studio(config);var api=new ToonTools(studio);
var outputSchema=JsonDocument.Parse("""
{"type":"object","oneOf":[{"required":["ok","data"],"properties":{"ok":{"const":true},"data":{}},"additionalProperties":false},{"required":["ok","error"],"properties":{"ok":{"const":false},"error":{"type":"object","required":["code","message","retryable","details"],"properties":{"code":{"type":"string"},"message":{"type":"string"},"retryable":{"type":"boolean"},"details":{"type":"object"}},"additionalProperties":false}},"additionalProperties":false}]}
""").RootElement;
var tools=typeof(ToonTools).GetMethods().Where(m=>m.GetCustomAttributes(typeof(McpServerToolAttribute),false).Length>0).Select(m=>McpServerTool.Create(m,api,new McpServerToolCreateOptions{UseStructuredContent=true,OutputSchema=outputSchema})).ToArray();
foreach(var tool in tools) {
    var input=JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
    input["additionalProperties"]=false;
    if(tool.ProtocolTool.Name is "create_project" or "update_project") {
        var draft=J.Read(Path.Combine(config.Root,"contracts/project.schema.json"));
        input["$defs"]=draft["$defs"]!.DeepClone();draft.AsObject().Remove("$defs");draft.AsObject().Remove("$id");draft.AsObject().Remove("$schema");input["properties"]!["project"]=draft;
    }
    if(tool.ProtocolTool.Name=="start_render")input["properties"]!["quality"]=J.Node(new{type="string",@enum=new[]{"preview","final"}});
    tool.ProtocolTool.InputSchema=JsonSerializer.SerializeToElement(input);
}
var builder=Host.CreateApplicationBuilder(args);builder.Logging.ClearProviders();builder.Logging.AddConsole(o=>o.LogToStandardErrorThreshold=LogLevel.Trace);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(tools).WithResources(new ToonResources(studio));
await builder.Build().RunAsync();

public sealed class ToonTools(Studio studio)
{
    private static async Task<CallToolResult> Call(Func<Task<JsonNode>> operation) {
        JsonNode result;bool isError=false;
        try{result=J.Node(new{ok=true,data=await operation()});}
        catch(Exception ex){isError=true;var e=ex as ToonException;result=J.Node(new{ok=false,error=new{code=e?.Code??"VALIDATION_ERROR",message=e?.Message??"Request could not be completed; verify input and local dependencies",retryable=e?.Retryable??false,details=new{pointer=e?.Pointer??""}}});}
        var content=new List<ContentBlock>{new TextContentBlock{Text=result.ToJsonString()}};
        if(!isError&&result["data"] is JsonArray artifacts)foreach(var item in artifacts.Where(x=>x?["resource_uri"]!=null)){var a=item!;content.Add(new ResourceLinkBlock{Uri=a.Str("resource_uri"),Name=a.Str("name"),MimeType=a.Str("mime")});}
        return new CallToolResult{IsError=isError,StructuredContent=JsonSerializer.SerializeToElement(result),Content=content};
    }
    private static Task<CallToolResult> Call(Func<JsonNode> operation)=>Call(()=>Task.FromResult(operation()));
    [McpServerTool(UseStructuredContent=true,Name="studio_doctor",ReadOnly=true),Description("Check local runtimes, renderer, fonts, worker heartbeat and voice readiness. Never returns secrets.")]
    public Task<CallToolResult> Doctor(CancellationToken cancellationToken)=>Call(()=>studio.Doctor(cancellationToken));
    [McpServerTool(UseStructuredContent=true,Name="get_catalog",ReadOnly=true),Description("Read versioned styles, characters, backgrounds, gestures or voices and the directing guide URI.")]
    public Task<CallToolResult> Catalog([Description("styles, characters, backgrounds, gestures or voices")]string kind)=>Call(()=>studio.Catalog(kind));
    [McpServerTool(UseStructuredContent=true,Name="register_asset",Destructive=false),Description("Import PNG/JPEG/WAV/MP3 from operator-configured TOON_IMPORT_ROOTS. Copy and hash immutable bytes. Optional JSON word alignment or phrase SRT; illustrative timing is rejected.")]
    public Task<CallToolResult> Register(string source_path,string kind,string? alignment_path=null,CancellationToken cancellationToken=default)=>Call(()=>studio.Assets.Register(source_path,kind,alignment_path,cancellationToken));
    [McpServerTool(UseStructuredContent=true,Name="create_project",Destructive=false),Description("Persist a DraftProject 1.0.0 as revision 1; structural/semantic errors fail, missing external dependencies may remain draft. Read the style guide first.")]
    public Task<CallToolResult> Create(JsonNode project)=>Call(()=>studio.Create(project));
    [McpServerTool(UseStructuredContent=true,Name="get_project",ReadOnly=true),Description("Read saved project revision and readiness. Omit revision to read latest.")]
    public Task<CallToolResult> Get(string project_id,int? revision=null)=>Call(()=>studio.GetProject(project_id,revision));
    [McpServerTool(UseStructuredContent=true,Name="update_project",Destructive=false),Description("Replace the complete draft, creating an immutable revision. expected_revision must equal current or REVISION_CONFLICT is returned.")]
    public Task<CallToolResult> Update(string project_id,int expected_revision,JsonNode project)=>Call(()=>studio.Update(project_id,expected_revision,project));
    [McpServerTool(UseStructuredContent=true,Name="validate_project",ReadOnly=true),Description("Check dependencies and estimate uncached TTS Unicode code points, without paid calls. Final timing/layout is checked after decoded speech preparation.")]
    public Task<CallToolResult> Validate(string project_id,int revision)=>Call(()=>studio.Validate(project_id,revision));
    [McpServerTool(UseStructuredContent=true,Name="start_render",Destructive=false,Idempotent=true),Description("Enqueue preview/final in independent worker. Same key+request returns same job; changed request conflicts. New paid speech is disabled unless explicitly allowed with a positive character limit. Does not wait for rendering.")]
    public Task<CallToolResult> Start(string project_id,int revision,string quality,string idempotency_key,bool allow_new_tts=false,int max_new_tts_characters=0)=>Call(()=>studio.Start(project_id,revision,quality,idempotency_key,allow_new_tts,max_new_tts_characters));
    [McpServerTool(UseStructuredContent=true,Name="get_job",ReadOnly=true),Description("Poll durable job state/stage, usage and artifacts. A failed job is a successful poll, not a tool error.")]
    public Task<CallToolResult> Job(string job_id)=>Call(()=>studio.Job(job_id));
    [McpServerTool(UseStructuredContent=true,Name="cancel_job",Idempotent=true),Description("Cancel queued work immediately or request controlled process-tree cancellation. Terminal states stay unchanged.")]
    public Task<CallToolResult> Cancel(string job_id)=>Call(()=>studio.PublicJob(studio.Store.Cancel(job_id)));
    [McpServerTool(UseStructuredContent=true,Name="get_artifacts",ReadOnly=true),Description("Return only published, hash-verified artifacts: MP4, PNG, project, timeline, captions, report and final ZIP. Never embeds media as base64.")]
    public Task<CallToolResult> Artifacts(string job_id)=>Call(()=>studio.Artifacts(job_id));
}

public sealed class ToonResources(Studio studio)
{
    [McpServerResource(UriTemplate="toon://styles/{id}/{version}/guide",Name="style_guide",MimeType="text/markdown"),Description("Versioned original style and directing rules")]
    public string Guide(string id,string version){J.Require(id=="warm_editorial_2d"&&version=="1.0.0","ASSET_MISSING","Unknown style guide");return File.ReadAllText(Path.Combine(studio.Config.Root,"styles/warm_editorial_2d/DIRECTING.md"));}
    [McpServerResource(UriTemplate="toon://projects/{id}/revisions/{revision}",Name="project_revision",MimeType="application/json"),Description("Immutable saved draft revision")]
    public string Project(string id,int revision)=>studio.Store.Project(id,revision).ToJsonString();
    [McpServerResource(UriTemplate="toon://jobs/{id}/report",Name="job_report",MimeType="application/json"),Description("Verified report or current job state")]
    public string Report(string id){var job=studio.Store.Job(id);var report=job.Items("artifacts").FirstOrDefault(a=>a.Str("name")=="validation-report.json");return report==null?studio.Job(id).ToJsonString():File.ReadAllText(report.Str("local_path"));}
}

