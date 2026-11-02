using System.Text;
using System.Text.RegularExpressions;

namespace Encoding.Api.Infrastructure.Data.Sqlite;

/// <summary>
/// Adapte les constructions SQL historiques utilisées par les dépôts aux fonctions SQLite (architecture 0043).
/// Les règles sont volontairement limitées aux constructions réellement utilisées par le backend ; une construction
/// inconnue échoue à l'exécution plutôt qu'en silence.
/// Les instants sont du texte UTC de largeur fixe (<see cref="Instant"/>) : l'ordre alphabétique est l'ordre chronologique.
/// </summary>
public static partial class SqliteTranslator
{
    public const string InfinityLiteral = "'9999-12-31 23:59:59.999999Z'";
    private const string ArrayPlaceholder = "/*ARRAY:";

    /// <summary>Instant courant (horloge de l'application côté base), éventuellement décalé de modificateurs SQLite.</summary>
    public static string Now(string? modifiers = null) =>
        "(strftime('%Y-%m-%d %H:%M:%f', 'now'" + (modifiers is null ? "" : ", " + modifiers) + ") || '000Z')";

    public static string NowUtc => Now();

    private static string SortedSet(string array) => $"(SELECT json_group_array(value) FROM (SELECT value FROM json_each({array}) ORDER BY value))";

    public static bool RequiresTranslation(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return false;
        return sql.Contains("clock_timestamp", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("now()", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("infinity", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("interval", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("make_interval", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("::", StringComparison.Ordinal)
            || sql.Contains("ANY(", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("jsonb_", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("gen_random_uuid", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("FOR SHARE", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("FOR NO KEY UPDATE", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("unnest", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("repeat(", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("ARRAY[", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("LEAST(", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("GREATEST(", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("chr(", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("<@", StringComparison.Ordinal)
            || sql.Contains(" - '", StringComparison.Ordinal)
            || sql.Contains("EXTRACT(", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("CROSS JOIN LATERAL", StringComparison.OrdinalIgnoreCase)
            || UpdateAliasRegex().IsMatch(sql);
    }

    public static string Translate(string sql)
    {
        if (!RequiresTranslation(sql)) return sql;
        var text = sql;
        text = InfinityRegex().Replace(text, InfinityLiteral);
        text = RowLockRegex().Replace(text, "");
        text = text.Replace("gen_random_uuid()", RandomUuid, StringComparison.Ordinal);
        text = Regex.Replace(text, @"EXTRACT\(\s*EPOCH\s+FROM\s+clock_timestamp\(\)\s*-\s*(min\(\w+\)|[\w.]+)\s*\)",
            "((julianday('now') - julianday($1)) * 86400.0)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"(@?\w+) <@ (@?\w+) AND \2 <@ \1", m => "(" + SortedSet(m.Groups[1].Value) + " = " + SortedSet(m.Groups[2].Value) + ")", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\brepeat\('([^'])',\s*(\d+)\)",
            m => $"replace(substr(hex(zeroblob({(int.Parse(m.Groups[2].Value) + 1) / 2})), 1, {m.Groups[2].Value}), '0', '{m.Groups[1].Value}')",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = text.Replace("now()", "clock_timestamp()", StringComparison.OrdinalIgnoreCase);
        text = TranslateIntervals(text);
        text = text.Replace("clock_timestamp()", Now(), StringComparison.Ordinal).Replace("now()", Now(), StringComparison.Ordinal);
        text = AnyColumnRegex().Replace(text, m => $"EXISTS(SELECT 1 FROM json_each({m.Groups[2].Value}) WHERE value = {m.Groups[1].Value})");
        text = AnyParameterRegex().Replace(text, m => $"{m.Groups[1].Value} IN ({ArrayPlaceholder}{m.Groups[2].Value}*/)");
        text = Regex.Replace(text, @"CROSS\s+JOIN\s+LATERAL\s+jsonb_each\(", ", json_each(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\bchr\(", "char(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\(\s*(@?[\w.]+)(?:::jsonb)?\s+-\s+'(\w+)'\s*\)", "json_remove($1, '$$.$2')", RegexOptions.CultureInvariant);
        text = UpdateAliasRegex().Replace(text, "UPDATE $1 AS $2 SET");
        text = UnnestRegex().Replace(text, "(SELECT value AS $3 FROM json_each(@$1)) AS $2");
        text = ArrayLiteralRegex().Replace(text, m => "json_array(" + m.Groups[1].Value + ")");
        text = CastRegex().Replace(text, "");
        text = LeastRegex().Replace(text, "MIN(");
        text = GreatestRegex().Replace(text, "MAX(");
        text = text.Replace("jsonb_array_length(", "json_array_length(", StringComparison.Ordinal)
                   .Replace("jsonb_typeof(", "json_type(", StringComparison.Ordinal);
        return text;
    }

    /// <summary>Noms des paramètres tableau à développer en <c>IN (@p_0, @p_1…)</c>.</summary>
    public static IReadOnlyList<string> ArrayParameters(string translated) =>
        translated.Contains(ArrayPlaceholder, StringComparison.Ordinal)
            ? ArrayMarkerRegex().Matches(translated).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    public static string ExpandArray(string translated, string name, int count)
    {
        var list = count == 0 ? "NULL" : string.Join(", ", Enumerable.Range(0, count).Select(i => $"@{name}_{i}"));
        return translated.Replace($"{ArrayPlaceholder}{name}*/", list, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Horloge SQL pour <c>clock_timestamp() + @param</c> où le paramètre est un <see cref="TimeSpan"/> converti en modificateur.</summary>
    public static string NowPlusParameter(string parameter) => Now($"@{parameter}");

    private static string TranslateIntervals(string text)
    {
        var result = new StringBuilder();
        var position = 0;
        foreach (Match match in ClockOffsetRegex().Matches(text))
        {
            if (match.Index < position) continue;
            var sign = match.Groups[1].Value;
            var rest = match.Index + match.Length;
            string? replacement = null;
            var end = rest;
            var literal = IntervalLiteralRegex().Match(text, rest);
            var makeInterval = MakeIntervalOpening().Match(text, rest);
            if (literal.Success && literal.Index == rest)
            {
                replacement = Now($"'{sign}{literal.Groups[1].Value} {Unit(literal.Groups[2].Value)}'");
                end = rest + literal.Length;
            }
            else if (makeInterval.Success && makeInterval.Index == rest)
            {
                var open = text.IndexOf('(', rest);
                var close = CloseParenthesis(text, open);
                var inner = text[(open + 1)..close];
                var argument = CastRegex().Replace(inner[(inner.IndexOf("=>", StringComparison.Ordinal) + 2)..], "");
                replacement = Now($"'{sign}' || ({argument}) || ' {Unit(makeInterval.Groups[1].Value)}'");
                end = close + 1;
            }
            else
            {
                var term = ReadTerm(text, rest, out var afterTerm);
                var times = TimesIntervalRegex().Match(text, afterTerm);
                if (times.Success && times.Index == afterTerm)
                {
                    replacement = Now($"'{sign}' || ({term}) || ' {Unit(times.Groups[1].Value)}'");
                    end = afterTerm + times.Length;
                }
                else if (TimeSpanRegex().Match(text, rest) is { Success: true } span && span.Index == rest)
                {
                    var name = span.Groups[1].Value;
                    replacement = sign == "+" ? NowPlusParameter(name)
                        : Now($"(CASE WHEN substr(@{name}, 1, 1) = '-' THEN '+' ELSE '-' END || substr(@{name}, 2))");
                    end = rest + span.Length;
                }
            }
            if (replacement is null) continue;
            result.Append(text, position, match.Index - position).Append(replacement);
            position = end;
        }
        result.Append(text, position, text.Length - position);
        return result.ToString();
    }

    private static string Unit(string unit) => unit.ToLowerInvariant() switch
    {
        "sec" or "secs" or "second" or "seconds" => "seconds",
        "min" or "mins" or "minute" or "minutes" => "minutes",
        "hour" or "hours" => "hours",
        "day" or "days" => "days",
        _ => throw new NotSupportedException($"Unité d'intervalle non prise en charge : {unit}.")
    };

    private static string ReadTerm(string text, int start, out int end)
    {
        var i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        var termStart = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '@' or '_' or '.')) i++;
        if (i < text.Length && text[i] == '(') i = CloseParenthesis(text, i) + 1;
        while (i < text.Length && text.AsSpan(i).StartsWith("::")) { i += 2; while (i < text.Length && char.IsLetter(text[i])) i++; }
        end = i;
        while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
        return CastRegex().Replace(text[termStart..i], "");
    }

    private static int CloseParenthesis(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '\'') { i = text.IndexOf('\'', i + 1); if (i < 0) break; continue; }
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        throw new FormatException("Parenthèses non équilibrées dans une requête SQL.");
    }

    private const string RandomUuid =
        "lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-' || substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6)))";

    [GeneratedRegex(@"'infinity'(::timestamptz)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex InfinityRegex();
    [GeneratedRegex(@"\s+FOR\s+(?:NO\s+KEY\s+)?(?:UPDATE|SHARE)\b(?:\s+OF\s+\w+(?:\s*,\s*\w+)*)?(?:\s+SKIP\s+LOCKED|\s+NOWAIT)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex RowLockRegex();
    [GeneratedRegex(@"clock_timestamp\(\)\s*([+-])\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ClockOffsetRegex();
    [GeneratedRegex(@"interval\s+'(\d+)\s+(\w+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex IntervalLiteralRegex();
    [GeneratedRegex(@"make_interval\(\s*(\w+)\s*=>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex MakeIntervalOpening();
    [GeneratedRegex(@"@(\w+)(?!\w)", RegexOptions.CultureInvariant)] private static partial Regex TimeSpanRegex();
    [GeneratedRegex(@"\*\s*interval\s+'1\s+(\w+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex TimesIntervalRegex();
    [GeneratedRegex(@"('[^']*'|@\w+)\s*=\s*ANY\(\s*([A-Za-z_][\w.]*|COALESCE\([^()]*\))\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex AnyColumnRegex();
    [GeneratedRegex(@"([A-Za-z_][\w.]*)\s*=\s*ANY\(\s*@(\w+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex AnyParameterRegex();
    [GeneratedRegex(@"::(?:uuid|text|jsonb|json|integer|int|bigint|numeric|double precision|timestamptz|timestamp|boolean|bytea)(?:\[\])?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CastRegex();
    [GeneratedRegex(@"\bARRAY\[([^\]]*)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ArrayLiteralRegex();
    [GeneratedRegex(@"unnest\(\s*@(\w+)(?:::text\[\])?\s*\)\s+AS\s+(\w+)\((\w+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex UnnestRegex();
    [GeneratedRegex(@"\bUPDATE\s+(\w+)\s+(?!AS\b|SET\b)(\w+)\s+SET\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex UpdateAliasRegex();
    [GeneratedRegex(@"\bLEAST\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LeastRegex();
    [GeneratedRegex(@"\bGREATEST\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex GreatestRegex();
    [GeneratedRegex(@"/\*ARRAY:(\w+)\*/", RegexOptions.CultureInvariant)] private static partial Regex ArrayMarkerRegex();
}
