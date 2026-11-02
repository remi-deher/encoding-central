using Encoding.Contracts.Flows;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime.Media.Handlers;

public sealed class ImageActionHandler(IProcessToolRunner tools, MediaScanner scanner) : IMediaActionHandler
{
    public bool CanHandle(string actionId) => actionId is
        "base-convert-image" or "base-resize-image" or "base-crop-image" or
        "image-convert" or "image-resize" or "image-crop";

    public async Task<MediaActionOutcome> ExecuteAsync(MediaActionContext context, CancellationToken ct)
    {
        var id = context.Id;
        var config = context.Config;
        var current = context.Current;
        var directory = context.Directory;
        var node = context.Node;

        var file = MediaActions.Output(directory, node, (id is "base-convert-image" or "image-convert") ? "." + MediaActions.Text(config, "format", "webp") : Path.GetExtension(current));
        var exe = tools.Find("magick") is not null ? "magick" : "convert";
        var args = new List<string> { current };

        if (id is "base-convert-image" or "image-convert")
            args.AddRange(["-quality", MediaActions.Number(config, "quality", 85)]);

        if (id is "base-resize-image" or "image-resize")
        {
            var resizeParam = MediaActions.Number(config, "width", 1920) + "x" + MediaActions.Number(config, "height", 1080)
                + (MediaActions.Flag(config, "preserveAspectRatio", true) ? ">" : "!");
            args.AddRange(["-resize", resizeParam]);
        }

        if (id is "base-crop-image" or "image-crop")
            await ConfigureCropArgumentsAsync(args, config, current, ct);

        args.Add(file);
        await tools.CheckedAsync(exe, args, directory, ct);
        return new(file, []);
    }

    private async Task ConfigureCropArgumentsAsync(List<string> args, IReadOnlyDictionary<string, System.Text.Json.JsonElement> config, string current, CancellationToken ct)
    {
        var ratio = MediaActions.Text(config, "aspectRatio");
        if (ratio != "")
        {
            var parts = ratio.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b) || a <= 0 || b <= 0)
                throw new MediaToolException("proportions de recadrage", 1);
            var scan = await scanner.ScanAsync(current, ct);
            var image = scan.GetProperty("streams")[0];
            var w = image.GetProperty("width").GetInt32();
            var h = image.GetProperty("height").GetInt32();
            var cropW = Math.Min(w, (int)((long)h * a / b));
            var cropH = Math.Min(h, (int)((long)w * b / a));
            args.AddRange(["-gravity", "center", "-crop", cropW + "x" + cropH + "+0+0", "+repage"]);
        }
        else
        {
            var cropParam = MediaActions.Number(config, "width", 1920) + "x" + MediaActions.Number(config, "height", 1080)
                + "+" + MediaActions.Number(config, "x") + "+" + MediaActions.Number(config, "y");
            args.AddRange(["-crop", cropParam, "+repage"]);
        }
    }
}
