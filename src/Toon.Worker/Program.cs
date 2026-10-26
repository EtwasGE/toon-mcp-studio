using Toon.Core;
using Toon.Infrastructure;

var config = new Config();
if (args.FirstOrDefault() == "slice")
{
    var directory = Path.GetFullPath(
        args.ElementAtOrDefault(1) ?? Path.Combine(config.Root, "examples/offline-dialogue")
    );
    var project = J.Read(Path.Combine(directory, "project.json"));
    Validation.Schema(project, Path.Combine(config.Root, "contracts/project.schema.json"));
    Validation.Semantic(project);
    var audio = new Dictionary<string, AudioInfo>();
    var media = new Media(config);
    foreach (var u in project.Items("scenes").SelectMany(s => s.Items("utterances")))
    {
        audio[u.Str("utterance_id")] = await media.Prepare(
            Path.Combine(directory, u["audio"]!["asset"]!.Str("asset_id") + ".wav"),
            null,
            CancellationToken.None
        );
    }
    var output = Path.Combine(config.Data, "stage1", args.ElementAtOrDefault(2) ?? "preview");
    await new Pipeline(config).Render(
        project,
        "offline_slice",
        1,
        audio,
        reference =>
            Path.Combine(
                directory,
                reference.Str("asset_id") == "demo_screen" ? "screen.png" : reference.Str("asset_id") + ".wav"
            ),
        output,
        args.ElementAtOrDefault(2) ?? "preview",
        Console.Error.WriteLine,
        CancellationToken.None
    );
    Console.WriteLine(output);
}
else
{
    using var stopped = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopped.Cancel();
    };
    try
    {
        await new Worker(new Studio(config)).Run(stopped.Token);
    }
    catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
}
