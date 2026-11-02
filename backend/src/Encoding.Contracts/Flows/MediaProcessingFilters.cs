using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encoding.Contracts.Flows;

public sealed record CropCoordinates(int Width, int Height, int X, int Y)
{
    public string ToFilter() => $"crop={Width}:{Height}:{X}:{Y}";

    public bool IsSignificant(int sourceWidth, int sourceHeight, int threshold = 16) =>
        Width > 0 && Height > 0 && (sourceWidth - Width >= threshold || sourceHeight - Height >= threshold);
}

public static partial class CropDetector
{
    private static readonly Regex CropRegex = new(@"crop=(?<w>\d+):(?<h>\d+):(?<x>\d+):(?<y>\d+)", RegexOptions.Compiled);

    public static CropCoordinates? ParseCropDetectLine(string line)
    {
        var match = CropRegex.Match(line);
        if (!match.Success) return null;
        return new(
            int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture)
        );
    }

    public static CropCoordinates? CalculateConsensus(IEnumerable<string> logLines, int roundMultiple = 2)
    {
        var crops = logLines
            .Select(ParseCropDetectLine)
            .Where(c => c is not null && c.Width > 0 && c.Height > 0)
            .Select(c => c!)
            .ToList();

        if (crops.Count == 0) return null;

        var sortedW = crops.Select(c => c.Width).OrderBy(v => v).ToList();
        var sortedH = crops.Select(c => c.Height).OrderBy(v => v).ToList();
        var sortedX = crops.Select(c => c.X).OrderBy(v => v).ToList();
        var sortedY = crops.Select(c => c.Y).OrderBy(v => v).ToList();

        int medianW = sortedW[sortedW.Count / 2];
        int medianH = sortedH[sortedH.Count / 2];
        int medianX = sortedX[sortedX.Count / 2];
        int medianY = sortedY[sortedY.Count / 2];

        if (roundMultiple > 1)
        {
            medianW = (medianW / roundMultiple) * roundMultiple;
            medianH = (medianH / roundMultiple) * roundMultiple;
            medianX = (medianX / roundMultiple) * roundMultiple;
            medianY = (medianY / roundMultiple) * roundMultiple;
        }

        return new(medianW, medianH, medianX, medianY);
    }
}

public static class DeinterlaceDetector
{
    private static readonly Regex IdetRegex = new(
        @"Multi frame detection:\s*TFF:\s*(?<tff>\d+)\s+BFF:\s*(?<bff>\d+)\s+Progressive:\s*(?<prog>\d+)\s+Undetermined:\s*(?<undet>\d+)",
        RegexOptions.Compiled);

    public static (int Tff, int Bff, int Progressive, int Undetermined)? ParseIdetOutput(string output)
    {
        var match = IdetRegex.Match(output);
        if (!match.Success) return null;
        return (
            int.Parse(match.Groups["tff"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["bff"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["prog"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["undet"].Value, CultureInfo.InvariantCulture)
        );
    }

    public static bool IsInterlaced(string output, double thresholdPercent = 10.0)
    {
        var parsed = ParseIdetOutput(output);
        if (parsed is null) return false;
        long interlaced = parsed.Value.Tff + parsed.Value.Bff;
        long total = interlaced + parsed.Value.Progressive + parsed.Value.Undetermined;
        if (total <= 0) return false;
        double ratio = (interlaced * 100.0) / total;
        return ratio >= thresholdPercent;
    }
}

public static class FileSizeGuard
{
    public static (bool Reduced, decimal PercentChange) Evaluate(long sourceBytes, long currentBytes, decimal minReductionPercent = 0m)
    {
        if (sourceBytes <= 0) return (false, 0m);
        decimal percentSaved = ((decimal)(sourceBytes - currentBytes) / sourceBytes) * 100m;
        bool reduced = percentSaved >= minReductionPercent;
        return (reduced, percentSaved);
    }
}

public static class LanguageFilterPlanner
{
    public static HashSet<int> IdentifyTracksToRemove(
        JsonElement streams,
        string trackType,
        string[] allowedLanguages,
        bool keepUndefined,
        bool keepFirstAudioFallback,
        bool keepForcedSubtitles)
    {
        var allowed = allowedLanguages
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToHashSet();

        var toRemove = new HashSet<int>();
        var audioTracks = new List<(int Index, string Lang, bool IsDefault)>();
        var subtitleTracks = new List<(int Index, string Lang, bool IsForced)>();

        foreach (var s in streams.EnumerateArray())
        {
            if (!s.TryGetProperty("codec_type", out var typeProp)) continue;
            var type = typeProp.GetString()?.ToLowerInvariant();
            if (!s.TryGetProperty("index", out var idxProp)) continue;
            var index = idxProp.GetInt32();

            string lang = "";
            if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("language", out var langProp))
                lang = (langProp.GetString() ?? "").Trim().ToLowerInvariant();

            bool isDefault = s.TryGetProperty("disposition", out var disp) && disp.ValueKind == JsonValueKind.Object
                && disp.TryGetProperty("default", out var dProp) && dProp.GetInt32() == 1;

            bool isForced = s.TryGetProperty("disposition", out var dispF) && dispF.ValueKind == JsonValueKind.Object
                && dispF.TryGetProperty("forced", out var fProp) && fProp.GetInt32() == 1;

            if (type == "audio") audioTracks.Add((index, lang, isDefault));
            if (type is "subtitle" or "subtitles") subtitleTracks.Add((index, lang, isForced));
        }

        if (trackType is "audio" or "all")
        {
            var keptAudio = new List<int>();
            foreach (var (idx, lang, isDefault) in audioTracks)
            {
                bool matches = allowed.Contains(lang);
                bool isUnd = string.IsNullOrEmpty(lang) || lang == "und";
                if (matches || (isUnd && keepUndefined))
                    keptAudio.Add(idx);
                else
                    toRemove.Add(idx);
            }

            if (keptAudio.Count == 0 && keepFirstAudioFallback && audioTracks.Count > 0)
            {
                var first = audioTracks[0].Index;
                toRemove.Remove(first);
            }
        }

        if (trackType is "subtitle" or "subtitles" or "all")
        {
            foreach (var (idx, lang, isForced) in subtitleTracks)
            {
                if (isForced && keepForcedSubtitles) continue;
                bool matches = allowed.Contains(lang);
                bool isUnd = string.IsNullOrEmpty(lang) || lang == "und";
                if (!matches && !(isUnd && keepUndefined))
                    toRemove.Add(idx);
            }
        }

        return toRemove;
    }
}

public static class CommentaryFilterPlanner
{
    private static readonly string[] DefaultKeywords =
    [
        "commentary", "commentaires", "director", "audio description",
        "audiodescription", "visual impaired", "dvs", "commentaire"
    ];

    public static HashSet<int> IdentifyCommentaryTracks(
        JsonElement streams,
        bool checkAudio = true,
        bool checkSubtitles = true,
        string[]? customKeywords = null)
    {
        var keywords = (customKeywords is { Length: > 0 } ? customKeywords : DefaultKeywords)
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => !string.IsNullOrEmpty(k))
            .ToArray();

        var toRemove = new HashSet<int>();

        foreach (var s in streams.EnumerateArray())
        {
            if (!s.TryGetProperty("codec_type", out var typeProp)) continue;
            var type = typeProp.GetString()?.ToLowerInvariant();
            if (!s.TryGetProperty("index", out var idxProp)) continue;
            var index = idxProp.GetInt32();

            bool isAudio = type == "audio";
            bool isSub = type is "subtitle" or "subtitles";
            if (!((isAudio && checkAudio) || (isSub && checkSubtitles))) continue;

            if (s.TryGetProperty("disposition", out var disp) && disp.ValueKind == JsonValueKind.Object)
            {
                if (disp.TryGetProperty("comment", out var cProp) && cProp.GetInt32() == 1)
                {
                    toRemove.Add(index);
                    continue;
                }
                if (disp.TryGetProperty("descriptions", out var dProp) && dProp.GetInt32() == 1)
                {
                    toRemove.Add(index);
                    continue;
                }
                if (disp.TryGetProperty("visual_impaired", out var vProp) && vProp.GetInt32() == 1)
                {
                    toRemove.Add(index);
                    continue;
                }
            }

            if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                string title = "";
                if (tags.TryGetProperty("title", out var tProp)) title = tProp.GetString() ?? "";
                if (tags.TryGetProperty("handler_name", out var hProp)) title += " " + (hProp.GetString() ?? "");

                title = title.ToLowerInvariant();
                if (keywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    toRemove.Add(index);
                }
            }
        }

        return toRemove;
    }
}
