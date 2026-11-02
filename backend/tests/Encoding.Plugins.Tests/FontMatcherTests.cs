using System.Buffers.Binary;
using Encoding.NodeAgent.Runtime.Media;
using static Encoding.Plugins.Tests.Support;

namespace Encoding.Plugins.Tests;

/// <summary>Tests unitaires pour la déduplication et l'identification des polices ASS (FontMatcher).</summary>
internal static class FontMatcherTests
{
    public static void Run()
    {
        OvergearedScenario_DeduplicatesEquivalentNames();
        StyleDistinction_DoesNotConfuseBoldAndBoldItalicOrRegular();
        ExtensionsAndPunctuation_MatchEquivalents();
        BinarySfntParser_ExtractsFamilyAndStyle();
        BinaryTtcParser_ExtractsCollectionFonts();
        InvalidOrDummyFile_GracefullyFallsBackToNormalizedFilename();
        Console.WriteLine("PASS: FontMatcher unit scenarios (Overgeared dedup, TTF name table, TTC collections, style distinction, extension/case variants).");
    }

    private static void OvergearedScenario_DeduplicatesEquivalentNames()
    {
        // Fichiers réels sur Overgeared S01E01 :
        // Présents dans le MKV : ArialBold.ttf, TrebuchetMSBold.ttf, ArialBoldItalic.ttf, TrebuchetMSBoldItalic.ttf, VerdanaBold.ttf
        // Ajoutés par la ressource Crunchyroll : Arial Bold.ttf, Times New Roman Bold.ttf, Trebuchet MS Bold.ttf
        var existing = new[]
        {
            FontMatcher.FromFileName("ArialBold.ttf", 751104),
            FontMatcher.FromFileName("TrebuchetMSBold.ttf", 143336),
            FontMatcher.FromFileName("ArialBoldItalic.ttf", 600000),
            FontMatcher.FromFileName("TrebuchetMSBoldItalic.ttf", 150000),
            FontMatcher.FromFileName("VerdanaBold.ttf", 300000),
        };

        var candidateArial = FontMatcher.FromFileName("Arial Bold.ttf", 751104);
        var candidateTrebuchet = FontMatcher.FromFileName("Trebuchet MS Bold.ttf", 143336);
        var candidateTimes = FontMatcher.FromFileName("Times New Roman Bold.ttf", 842168);

        var matchArial = existing.FirstOrDefault(e => FontMatcher.IsEquivalent(candidateArial, e));
        Check(matchArial is not null && matchArial.FileName == "ArialBold.ttf", "Arial Bold.ttf matches existing ArialBold.ttf");

        var matchTrebuchet = existing.FirstOrDefault(e => FontMatcher.IsEquivalent(candidateTrebuchet, e));
        Check(matchTrebuchet is not null && matchTrebuchet.FileName == "TrebuchetMSBold.ttf", "Trebuchet MS Bold.ttf matches existing TrebuchetMSBold.ttf");

        var matchTimes = existing.FirstOrDefault(e => FontMatcher.IsEquivalent(candidateTimes, e));
        Check(matchTimes is null, "Times New Roman Bold.ttf has no equivalent in MKV and is not matched");
    }

    private static void StyleDistinction_DoesNotConfuseBoldAndBoldItalicOrRegular()
    {
        var arialBold = FontMatcher.FromFileName("ArialBold.ttf");
        var arialBoldItalic = FontMatcher.FromFileName("ArialBoldItalic.ttf");
        var arialRegular = FontMatcher.FromFileName("Arial.ttf");
        var arialItalic = FontMatcher.FromFileName("ArialItalic.ttf");

        Check(!FontMatcher.IsEquivalent(arialBold, arialBoldItalic), "Bold is not confused with Bold Italic");
        Check(!FontMatcher.IsEquivalent(arialBold, arialRegular), "Bold is not confused with Regular");
        Check(!FontMatcher.IsEquivalent(arialBold, arialItalic), "Bold is not confused with Italic");
        Check(!FontMatcher.IsEquivalent(arialItalic, arialBoldItalic), "Italic is not confused with Bold Italic");

        var trebBold = FontMatcher.FromFileName("Trebuchet MS Bold.ttf");
        var trebBoldItalic = FontMatcher.FromFileName("Trebuchet MS Bold Italic.ttf");
        var trebRegular = FontMatcher.FromFileName("Trebuchet MS.ttf");

        Check(!FontMatcher.IsEquivalent(trebBold, trebBoldItalic), "Trebuchet Bold != Trebuchet Bold Italic");
        Check(!FontMatcher.IsEquivalent(trebBold, trebRegular), "Trebuchet Bold != Trebuchet Regular");
    }

    private static void ExtensionsAndPunctuation_MatchEquivalents()
    {
        var ttf = FontMatcher.FromFileName("Arial Bold.ttf");
        var otf = FontMatcher.FromFileName("Arial-Bold.otf");
        var ttc = FontMatcher.FromFileName("Arial_Bold.ttc");
        var upper = FontMatcher.FromFileName("ARIAL-BOLD.TTF");
        var compact = FontMatcher.FromFileName("arialbold.ttf");
        var spaced = FontMatcher.FromFileName("Arial   Bold.ttf");

        Check(FontMatcher.IsEquivalent(ttf, otf), ".ttf and .otf with dash match");
        Check(FontMatcher.IsEquivalent(ttf, ttc), ".ttf and .ttc with underscore match");
        Check(FontMatcher.IsEquivalent(ttf, upper), "Uppercase matches lowercase");
        Check(FontMatcher.IsEquivalent(ttf, compact), "Compact matches spaced");
        Check(FontMatcher.IsEquivalent(ttf, spaced), "Multiple spaces match");
    }

    private static void BinarySfntParser_ExtractsFamilyAndStyle()
    {
        var ttfBytes = CreateSyntheticTtf("Arial", "Bold");
        var parsed = FontMatcher.ParseFontBytes(ttfBytes, "arbitrary-name.ttf", ttfBytes.Length);
        Check(parsed is not null, "Synthetic TTF is parsed");
        Check(parsed!.Family == "Arial", "Family is Arial");
        Check(parsed.Style == "Bold", "Style is Bold");
        Check(parsed.FullName == "Arial Bold", "FullName is Arial Bold");
        Check(parsed.MatchKeys.Contains("arialbold"), "Keys contain arialbold");

        // Même avec un nom de fichier non conventionnel, la comparaison par métadonnées fonctionne
        var candidateFromResource = FontMatcher.FromFileName("Arial Bold.ttf");
        Check(FontMatcher.IsEquivalent(parsed, candidateFromResource), "Font with arbitrary filename matches candidate via TTF metadata");
    }

    private static void BinaryTtcParser_ExtractsCollectionFonts()
    {
        var font1 = CreateSyntheticTtf("Cambria", "Regular");
        var font2 = CreateSyntheticTtf("Cambria Math", "Regular");
        var ttcBytes = CreateSyntheticTtc([font1, font2]);

        var parsed = FontMatcher.ParseFontBytes(ttcBytes, "cambria.ttc", ttcBytes.Length);
        Check(parsed is not null, "TTC is parsed");
        Check(parsed!.MatchKeys.Contains("cambria"), "Keys contain cambria");
        Check(parsed.MatchKeys.Contains("cambriamath"), "Keys contain cambriamath");

        Check(FontMatcher.IsEquivalent(parsed, FontMatcher.FromFileName("Cambria.ttf")), "Matches Cambria");
        Check(FontMatcher.IsEquivalent(parsed, FontMatcher.FromFileName("Cambria Math.ttf")), "Matches Cambria Math");
        Check(!FontMatcher.IsEquivalent(parsed, FontMatcher.FromFileName("Arial.ttf")), "Does not match Arial");
    }

    private static void InvalidOrDummyFile_GracefullyFallsBackToNormalizedFilename()
    {
        var dummyBytes = new byte[2048];
        var parsed = FontMatcher.ParseFontBytes(dummyBytes, "CustomFont-Bold.ttf", 2048);
        Check(parsed is null, "Dummy bytes return null from binary parser");

        var tempFile = Path.Combine(Path.GetTempPath(), "dummy-test-font-" + Guid.NewGuid().ToString("N") + ".ttf");
        try
        {
            File.WriteAllBytes(tempFile, dummyBytes);
            var desc = FontMatcher.Read(tempFile, "CustomFont-Bold.ttf");
            Check(desc.MatchKeys.Contains("customfontbold"), "Fallback descriptor uses normalized filename");
            Check(desc.Family is null, "Family is null on fallback");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    public static byte[] CreateSyntheticTtf(string family, string style)
    {
        var fullName = string.IsNullOrEmpty(style) || style == "Regular" ? family : family + " " + style;
        var strings = new (ushort nameId, byte[] data)[]
        {
            (1, System.Text.Encoding.BigEndianUnicode.GetBytes(family)),
            (2, System.Text.Encoding.BigEndianUnicode.GetBytes(style)),
            (4, System.Text.Encoding.BigEndianUnicode.GetBytes(fullName))
        };

        var strStorage = new MemoryStream();
        var records = new List<(ushort nameId, ushort len, ushort offset)>();
        foreach (var s in strings)
        {
            var off = (ushort)strStorage.Position;
            strStorage.Write(s.data);
            records.Add((s.nameId, (ushort)s.data.Length, off));
        }

        var nameTable = new MemoryStream();
        WriteU16(nameTable, 0); // format 0
        WriteU16(nameTable, (ushort)records.Count);
        WriteU16(nameTable, (ushort)(6 + records.Count * 12)); // stringOffset

        foreach (var r in records)
        {
            WriteU16(nameTable, 3); // Windows platform
            WriteU16(nameTable, 1); // Unicode BMP
            WriteU16(nameTable, 0x0409); // English US
            WriteU16(nameTable, r.nameId);
            WriteU16(nameTable, r.len);
            WriteU16(nameTable, r.offset);
        }
        var strBytes = strStorage.ToArray();
        nameTable.Write(strBytes);
        var nameBytes = nameTable.ToArray();

        var fontStream = new MemoryStream();
        fontStream.Write(new byte[] { 0x00, 0x01, 0x00, 0x00 }); // sfntVersion 0x00010000
        WriteU16(fontStream, 1); // numTables = 1
        WriteU16(fontStream, 16); // searchRange
        WriteU16(fontStream, 0); // entrySelector
        WriteU16(fontStream, 0); // rangeShift

        // Entry for table "name"
        fontStream.Write(System.Text.Encoding.ASCII.GetBytes("name"));
        WriteU32(fontStream, 0); // checksum
        WriteU32(fontStream, 28); // offset = 28
        WriteU32(fontStream, (uint)nameBytes.Length); // length
        fontStream.Write(nameBytes);

        return fontStream.ToArray();
    }

    private static byte[] CreateSyntheticTtc(IReadOnlyList<byte[]> fonts)
    {
        var ttcStream = new MemoryStream();
        ttcStream.Write(System.Text.Encoding.ASCII.GetBytes("ttcf"));
        WriteU16(ttcStream, 1); // majorVersion
        WriteU16(ttcStream, 0); // minorVersion
        WriteU32(ttcStream, (uint)fonts.Count);

        var offsetTablePos = ttcStream.Position;
        // Placeholders for offsets
        for (var i = 0; i < fonts.Count; i++) WriteU32(ttcStream, 0);

        var offsets = new List<uint>();
        foreach (var font in fonts)
        {
            var offset = (uint)ttcStream.Position;
            offsets.Add(offset);
            ttcStream.Write(font);
        }

        var result = ttcStream.ToArray();
        for (var i = 0; i < offsets.Count; i++)
        {
            var pos = (int)(offsetTablePos + i * 4);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos, 4), offsets[i]);
            // Ajuster l'offset absolu de la table "name" dans le TTC (tag à +12, checksum à +16, offset à +20)
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan((int)offsets[i] + 20, 4), offsets[i] + 28);
        }
        return result;
    }

    private static void WriteU16(Stream s, ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteU32(Stream s, uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }
}
