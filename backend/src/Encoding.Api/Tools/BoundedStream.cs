namespace Encoding.Api.Tools;

/// <summary>Lecture bornée d'un flux : un fournisseur ne peut pas faire allouer une réponse sans limite.</summary>
internal static class BoundedStream
{
    private const int MaxBytes = 16 * 1024 * 1024;

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > MaxBytes) throw new ArgumentException("Message IA trop volumineux.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }
}
