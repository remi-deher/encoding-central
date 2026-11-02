using Encoding.Api.Infrastructure.Data.Sqlite;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Architecture 0043 : adaptation des requêtes aux fonctions SQLite, sans base.</summary>
public sealed class SqliteTranslatorTests
{
    [Fact]
    public void RowLocksDisappear()
    {
        Assert.DoesNotContain("FOR UPDATE", SqliteTranslator.Translate("SELECT 1 FROM t WHERE a = 1 FOR UPDATE OF t SKIP LOCKED"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InfinityBecomesTheLatestInstant()
    {
        var sql = SqliteTranslator.Translate("UPDATE t SET until = 'infinity'::timestamptz");
        Assert.Contains("9999-12-31 23:59:59.999999Z", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("infinity", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("clock_timestamp() + interval '60 seconds'", "'+60 seconds'")]
    [InlineData("clock_timestamp() - interval '1 minute'", "'-1 minutes'")]
    [InlineData("now() - interval '60 days'", "'-60 days'")]
    [InlineData("clock_timestamp() + make_interval(mins => @delay)", "' minutes'")]
    [InlineData("clock_timestamp() + @minutes * interval '1 minute'", "' minutes'")]
    public void IntervalsBecomeDateModifiers(string expression, string expected)
    {
        var sql = SqliteTranslator.Translate("SELECT " + expression);
        Assert.Contains(expected, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("interval", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClockMinusTimeSpanParameterNegatesTheModifier()
    {
        var sql = SqliteTranslator.Translate("last_seen_at > clock_timestamp() - @offline");
        Assert.Contains("substr(@offline, 2)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("clock_timestamp", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ArraysUseJsonFunctions()
    {
        Assert.Contains("json_each(roles)", SqliteTranslator.Translate("SELECT 1 FROM n WHERE 'worker' = ANY(roles)"), StringComparison.Ordinal);
        Assert.Contains("json_array('worker')", SqliteTranslator.Translate("INSERT INTO n(roles) VALUES(ARRAY['worker'])"), StringComparison.Ordinal);
        Assert.Contains("json_each(@plugins)", SqliteTranslator.Translate("FROM unnest(@plugins::text[]) AS requested(plugin_id)"), StringComparison.Ordinal);
        var any = SqliteTranslator.Translate("WHERE m.id = ANY(@ids)");
        Assert.Equal(["ids"], SqliteTranslator.ArrayParameters(any));
        Assert.Equal("WHERE m.id IN (@ids_0, @ids_1)", SqliteTranslator.ExpandArray(any, "ids", 2));
        Assert.Equal("WHERE m.id IN (NULL)", SqliteTranslator.ExpandArray(any, "ids", 0));
    }

    [Fact]
    public void HistoricalSqlExpressionsAreTranslatedToSqlite()
    {
        Assert.DoesNotContain("::", SqliteTranslator.Translate("SELECT a::text, b::integer FROM t"), StringComparison.Ordinal);
        Assert.StartsWith("UPDATE execution_work AS w SET", SqliteTranslator.Translate("UPDATE execution_work w SET a = 1"), StringComparison.Ordinal);
        Assert.Contains("MIN(a, b)", SqliteTranslator.Translate("SELECT LEAST(a, b)"), StringComparison.Ordinal);
        Assert.Contains("json_remove(definition, '$.version')", SqliteTranslator.Translate("SELECT (definition - 'version') FROM v"), StringComparison.Ordinal);
        Assert.Contains("replace(substr(hex(zeroblob(336)), 1, 672), '0', '1')", SqliteTranslator.Translate("SELECT repeat('1',672)"), StringComparison.Ordinal);
    }

    [Fact]
    public void TranslationIsStableForUntouchedStatements()
    {
        const string plain = "SELECT id, name FROM libraries WHERE id = @id ORDER BY name";
        Assert.Equal(plain, SqliteTranslator.Translate(plain));
    }

    [Fact]
    public void PlainStatementsBypassTranslation()
    {
        const string plain = "SELECT id, name FROM libraries WHERE id = @id ORDER BY name";
        Assert.False(SqliteTranslator.RequiresTranslation(plain));
        Assert.Same(plain, SqliteTranslator.Translate(plain));
    }
}
