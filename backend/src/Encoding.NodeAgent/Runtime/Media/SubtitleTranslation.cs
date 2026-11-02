using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed record SubtitleTranslationOutcome(string? File, string? ErrorCode = null);

/// <summary>Translates standalone SRT files in bounded batches while keeping cue ids and timecodes worker-owned.</summary>
public static partial class SubtitleTranslation
{
    private sealed record Cue(string Id, string Timecode, string Text);

    [GeneratedRegex(@"(?:^|\r?\n)(?<id>\d+)\r?\n(?<timecode>\d{2}:\d{2}:\d{2}[,\.]\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}[,\.]\d{3})\r?\n(?<text>[\s\S]*?)(?=(?:\r?\n\d+\r?\n\d{2}:\d{2}:\d{2}|$))",
        RegexOptions.CultureInvariant)]
    private static partial Regex SrtCueRegex();

    [GeneratedRegex("</?[A-Za-z][^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupRegex();

    public static async Task<SubtitleTranslationOutcome> TranslateAsync(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> configuration,
        string sourcePath,
        string directory,
        string flowNodeId,
        Func<int, IReadOnlyList<RuntimeAiSubtitleItem>, Task<RuntimeAiSubtitleTranslationResponse>> translateBatch,
        CancellationToken ct)
    {
        if (!string.Equals(Path.GetExtension(sourcePath), ".srt", StringComparison.OrdinalIgnoreCase))
            throw new SubtitleException("Le bloc de traduction IA accepte actuellement les fichiers SRT autonomes.");
        var info = new FileInfo(sourcePath);
        if (info.Length is 0 or > 8 * 1024 * 1024)
            throw new SubtitleException("Le fichier SRT est vide ou dépasse la limite de 8 Mio.");
        var source = await File.ReadAllTextAsync(sourcePath, ct);
        var matches = SrtCueRegex().Matches(source.Trim().TrimStart('\uFEFF'));
        var cues = matches.Select(match => new Cue(match.Groups["id"].Value,
            match.Groups["timecode"].Value.Trim(), match.Groups["text"].Value.Trim())).ToArray();
        if (cues.Length == 0 || cues.Length > 50_000
            || cues.Select(cue => cue.Id).Distinct(StringComparer.Ordinal).Count() != cues.Length
            || cues.Any(cue => cue.Text.Length is 0 or > 4_000))
            throw new SubtitleException("Le SRT ne contient pas de répliques valides ou dépasse les limites de traduction.");

        var batchSize = configuration.TryGetValue("batchSize", out var batchValue)
            && batchValue.ValueKind == System.Text.Json.JsonValueKind.Number
            && batchValue.TryGetInt32(out var configuredBatch) ? Math.Clamp(configuredBatch, 20, 200) : 80;
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        long translatedCharacters = 0;
        var batchIndex = 0;
        for (var offset = 0; offset < cues.Length;)
        {
            ct.ThrowIfCancellationRequested();
            if (batchIndex >= 100) throw new SubtitleException("Le flow dépasse le budget de 100 requêtes IA par fichier.");
            var batch = new List<Cue>(batchSize);
            var characters = 0;
            while (offset < cues.Length && batch.Count < batchSize
                && characters + cues[offset].Text.Length <= 24_000)
            {
                batch.Add(cues[offset]);
                characters += cues[offset].Text.Length;
                offset++;
            }
            if (batch.Count == 0) throw new SubtitleException("Une réplique dépasse la taille maximale d’un lot.");
            var request = batch.Select(cue => new RuntimeAiSubtitleItem(cue.Id, cue.Text)).ToArray();
            var response = await translateBatch(batchIndex++, request);
            if (!response.Success || response.Items is null) return new(null, response.ErrorCode ?? "error");
            var byId = response.Items.ToDictionary(item => item.Id, item => item.Text, StringComparer.Ordinal);
            if (byId.Count != batch.Count || batch.Any(cue => !byId.ContainsKey(cue.Id)))
                throw new SubtitleException("La réponse du service IA ne correspond pas aux répliques du lot.");
            foreach (var cue in batch)
            {
                var translated = byId[cue.Id];
                if (!MarkupRegex().Matches(cue.Text).Select(match => match.Value)
                    .SequenceEqual(MarkupRegex().Matches(translated).Select(match => match.Value), StringComparer.Ordinal))
                    throw new SubtitleException("La traduction a modifié le balisage d’une réplique; le fichier source est conservé.");
                translatedCharacters += translated.Length;
                if (translatedCharacters > 16 * 1024 * 1024)
                    throw new SubtitleException("Le texte traduit dépasse la limite de sortie de 16 Mio.");
                translations.Add(cue.Id, translated);
            }
        }

        var output = new StringBuilder();
        foreach (var cue in cues)
        {
            output.AppendLine(cue.Id);
            output.AppendLine(cue.Timecode);
            output.AppendLine(translations[cue.Id]);
            output.AppendLine();
        }
        var suffix = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(flowNodeId)))[..16];
        var target = Path.Combine(directory, "translated-" + suffix + ".srt");
        await File.WriteAllTextAsync(target, output.ToString(), new UTF8Encoding(false), ct);
        return new(target);
    }

    /// <summary>Extracts one text subtitle stream, translates it, then remuxes it beside the untouched source streams.</summary>
    public static async Task<SubtitleTranslationOutcome> TranslateEmbeddedAsync(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> configuration,
        string sourcePath, string directory, string flowNodeId,
        System.Text.Json.JsonElement mediaMetadata, ProcessToolRunner tools,
        Func<int, IReadOnlyList<RuntimeAiSubtitleItem>, Task<RuntimeAiSubtitleTranslationResponse>> translateBatch,
        CancellationToken ct)
    {
        var streams = mediaMetadata.GetProperty("streams").EnumerateArray()
            .Where(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() == "subtitle")
            .ToArray();
        var selectedIndex = configuration.TryGetValue("subtitleTrackIndex", out var selected)
            && selected.ValueKind == System.Text.Json.JsonValueKind.Number && selected.TryGetInt32(out var configured) ? configured : 0;
        if (selectedIndex < 0 || selectedIndex >= streams.Length)
            throw new SubtitleException("L’indice de piste de sous-titres intégrée est invalide.");
        var stream = streams[selectedIndex];
        var codec = stream.TryGetProperty("codec_name", out var codecElement) ? codecElement.GetString() : null;
        if (codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub")
            throw new SubtitleException("La piste sélectionnée contient des sous-titres image; une étape OCR est nécessaire.");
        var streamIndex = stream.GetProperty("index").GetInt32();
        var token = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(flowNodeId)))[..16];
        var extracted = Path.Combine(directory, "subtitle-" + token + ".srt");
        await tools.CheckedAsync("ffmpeg", ["-nostdin", "-v", "error", "-i", sourcePath, "-map", "0:" + streamIndex,
            "-c:s", "srt", "-f", "srt", "-y", extracted], directory, ct);
        var translated = await TranslateAsync(configuration, extracted, directory, flowNodeId, translateBatch, ct);
        if (translated.ErrorCode is not null || translated.File is null) return translated;

        var extension = Path.GetExtension(sourcePath);
        if (!extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase))
            throw new SubtitleException("Le remuxage des sous-titres traduits prend actuellement en charge MKV et MP4.");
        var target = Path.Combine(directory, "translated-media-" + token + extension);
        var existingSubtitles = streams.Length;
        var targetCodec = extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ? "mov_text" : "srt";
        var language = configuration.TryGetValue("targetLanguage", out var languageValue)
            && languageValue.ValueKind == System.Text.Json.JsonValueKind.String ? languageValue.GetString() : "und";
        if (language is null || language.Length is < 2 or > 3 || language.Any(character => !char.IsAsciiLetter(character))) language = "und";
        await tools.CheckedAsync("ffmpeg", ["-nostdin", "-v", "error", "-i", sourcePath, "-i", translated.File,
            "-map", "0", "-map", "1:0", "-c", "copy", "-c:s:" + existingSubtitles, targetCodec,
            "-metadata:s:s:" + existingSubtitles, "language=" + language,
            "-metadata:s:s:" + existingSubtitles, "title=Translated subtitles", "-y", target], directory, ct);
        return new(target);
    }
}
