using Microsoft.Net.Http.Headers;

namespace Encoding.Api.Infrastructure;

/// <summary>Point unique par lequel les endpoints servent un fichier du disque : ils ne touchent pas au système de fichiers.</summary>
public static class FileResults
{
    public static bool Exists(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    public static IResult ServeOrNotFound(string path, string contentType, string? downloadName = null, string? etag = null)
        => Exists(path)
            ? Results.File(path, contentType, downloadName, enableRangeProcessing: true, entityTag: etag is null ? null : new EntityTagHeaderValue(etag))
            : Results.NotFound();
}
