using System.Text;
using System.Text.RegularExpressions;

namespace Encoding.Api.Flows.Subtitles;

public sealed record SubtitleEntry(int Index, string Timecode, string Text);

public static class SubtitleChunker
{
    private static readonly Regex SrtBlockRegex = new(
        @"(?:^|\r?\n)(?<index>\d+)\r?\n(?<timecode>\d{2}:\d{2}:\d{2}[,\.]\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}[,\.]\d{3})\r?\n(?<text>[\s\S]*?)(?=(?:\r?\n\d+\r?\n\d{2}:\d{2}:\d{2}|$))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<SubtitleEntry> ParseSrt(string srtContent)
    {
        var entries = new List<SubtitleEntry>();
        if (string.IsNullOrWhiteSpace(srtContent)) return entries;

        var matches = SrtBlockRegex.Matches(srtContent.Trim());
        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups["index"].Value, out var idx))
            {
                var tc = m.Groups["timecode"].Value.Trim();
                var txt = m.Groups["text"].Value.Trim();
                entries.Add(new SubtitleEntry(idx, tc, txt));
            }
        }
        return entries;
    }

    public static IReadOnlyList<IReadOnlyList<SubtitleEntry>> ChunkEntries(IReadOnlyList<SubtitleEntry> entries, int chunkSize = 80)
    {
        if (chunkSize <= 0) chunkSize = 80;
        var chunks = new List<IReadOnlyList<SubtitleEntry>>();
        for (var i = 0; i < entries.Count; i += chunkSize)
        {
            chunks.Add(entries.Skip(i).Take(chunkSize).ToList());
        }
        return chunks;
    }

    public static string FormatChunkForPrompt(IReadOnlyList<SubtitleEntry> chunk)
    {
        var sb = new StringBuilder();
        foreach (var entry in chunk)
        {
            sb.AppendLine(entry.Index.ToString());
            sb.AppendLine(entry.Timecode);
            sb.AppendLine(entry.Text);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string ReassembleSrt(IEnumerable<string> translatedChunks)
    {
        var sb = new StringBuilder();
        foreach (var chunk in translatedChunks)
        {
            var trimmed = chunk.Trim();
            if (trimmed.Length > 0)
            {
                sb.AppendLine(trimmed);
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}
