using System.Text;
using System.Text.RegularExpressions;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// ADR 0043 : le SQL du dépôt est du SQLite natif, exécuté tel quel. Ce test extrait chaque requête écrite dans <c>backend/src</c>
/// (littéraux, chaînes interpolées et concaténations), la fait compiler par SQLite (<c>EXPLAIN</c> = <c>sqlite3_prepare</c>, sans exécution)
/// sur une base migrée, puis échoue sur toute erreur de syntaxe, table, colonne ou fonction inconnue : l'erreur apparaît en CI,
/// pas à l'exécution. Les restes de dialecte PostgreSQL (casts, <c>FOR UPDATE</c>, <c>jsonb_*</c>…) sont aussi refusés explicitement.
/// </summary>
public sealed class SqlStatementsPrepareTests
{
    private const int MinimumStatements = 250;

    private static readonly Regex StatementStart = new(@"^\s*(WITH|SELECT|INSERT|UPDATE|DELETE)\b[\s\S]*\b(FROM|INTO|SET)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Parameter = new(@"(?<![\w'])@([A-Za-z_]\w*)", RegexOptions.CultureInvariant);

    private static readonly Regex PostgresDialect = new(
        @"::|\bclock_timestamp\b|\bnow\s*\(\s*\)|\binterval\s+'|\bmake_interval\b|\bjsonb_\w+|\bILIKE\b|\bANY\s*\(|\bFOR\s+(NO\s+KEY\s+)?(UPDATE|SHARE)\b|\bSKIP\s+LOCKED\b"
        + @"|\bLATERAL\b|\bunnest\b|\bARRAY\s*\[|\bLEAST\s*\(|\bGREATEST\s*\(|\bgen_random_uuid\b|\btimestamptz\b|\bEXTRACT\s*\(|\binfinity\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Fragments SQL construits à l'exécution (variables locales, méthodes) : valeur représentative par « Fichier.cs::expression ».
    /// Une expression non résolue fait échouer le test : l'ajouter ici avec une valeur valide plutôt que de contourner la vérification.
    /// </summary>
    private static readonly Dictionary<string, string> DynamicFragments = new(StringComparer.Ordinal)
    {
        ["ExecutionRepository.cs::ActivelyProcessingSql(\"e\")"] = "(e.state='running' AND w.worker_id IS NOT NULL AND w.lease_until>" + Sql.UtcNow + ")",
        ["ExecutionRepository.cs::whereSql"] = " WHERE e.state=@state AND w.library_id=@libraryId AND e.worker_id=@nodeId AND e.updated_at>=@since",
        ["ExecutionRepository.cs::filterSql"] = " AND revision>@afterRevision",
        ["ExecutionRepository.cs::limitSql"] = " LIMIT @limit",
        ["FlowBackupService.cs::table"] = "flow_backup_jobs",
        ["FlowBackupService.cs::number"] = "version",
        ["LibraryActivityRepository.cs::filterSql"] = " WHERE library_id=@libraryId AND flow=@flow AND worker_id=@nodeId",
        ["LibraryActivityRepository.cs::resultFilterSql"] = " WHERE library_id=@libraryId AND status=@status",
        ["LibraryWorkflowRepository.cs::searchFilter"] = " AND lower(relative_path) LIKE @search",
        ["LibraryWorkflowRepository.cs::string.Join(\",\", values)"] = "(@id0,@library,@path0,@bytes0,@modified0,@scan)",
        ["RuntimeRepository.Claim.cs::cursorPredicate"] = "AND (e.queued_at,e.id) > (@after_time,@after_id)",
        ["WorkerRepository.cs::requestPredicate"] = "worker_id = @worker_id",
        ["DatabaseTransfer.cs::Quote(table)"] = "\"workers\"",
        ["DatabaseTransfer.cs::quotedTable"] = "\"workers\"",
        ["DatabaseTransfer.cs::table"] = "workers",
        ["DatabaseTransfer.cs::names"] = "\"id\", \"name\"",
        ["DatabaseTransfer.cs::parameters"] = "@value_0, @value_1",
    };

    private sealed record Statement(string File, int Line, string Sql, IReadOnlyList<string> Unresolved);

    [DatabaseFact]
    public async Task EverySqlStatementOfTheBackendPreparesOnAMigratedDatabase()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("sql-prepare");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();

        var statements = ExtractStatements();
        Assert.True(statements.Count >= MinimumStatements,
            $"Seulement {statements.Count} requêtes SQL détectées (minimum {MinimumStatements}) : l'extraction ne voit plus les sources.");

        var failures = new List<string>();
        foreach (var statement in statements)
        {
            if (statement.Unresolved.Count > 0)
            {
                failures.Add($"{statement.File}:{statement.Line} fragment SQL dynamique non déclaré dans DynamicFragments : {string.Join(", ", statement.Unresolved)}");
                continue;
            }
            var error = Prepare(connection, statement.Sql);
            if (error is not null) failures.Add($"{statement.File}:{statement.Line} {error} :: {Preview(statement.Sql)}");
        }
        Assert.True(failures.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void NoPostgresDialectRemainsInSqlStatements()
    {
        var offenders = ExtractStatements()
            .Select(statement => (statement, match: PostgresDialect.Match(statement.Sql)))
            .Where(entry => entry.match.Success)
            .Select(entry => $"{entry.statement.File}:{entry.statement.Line} « {entry.match.Value} » :: {Preview(entry.statement.Sql)}")
            .ToArray();
        Assert.True(offenders.Length == 0, "Dialecte PostgreSQL interdit (SQL natif SQLite uniquement) :" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [DatabaseFact]
    public async Task PrepareRejectsSyntaxErrorsAndForeignDialect()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("sql-prepare-guard");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();

        Assert.Null(Prepare(connection, "SELECT id FROM workers WHERE id = @id"));
        Assert.NotNull(Prepare(connection, "SELEC id FROM workers"));
        Assert.NotNull(Prepare(connection, "SELECT id FROM no_such_table"));
        Assert.NotNull(Prepare(connection, "SELECT no_such_column FROM workers"));
        Assert.NotNull(Prepare(connection, "UPDATE workers SET last_seen_at = clock_timestamp()"));
        Assert.NotNull(Prepare(connection, "SELECT id FROM workers FOR UPDATE SKIP LOCKED"));
        Assert.NotNull(Prepare(connection, "SELECT id::text FROM workers"));
    }

    [DatabaseFact]
    public async Task MigrationNativeScheduleSlotsDefaultOpensEverySlot()
    {
        await using var database = await TestDatabase.CreateSqliteAsync("sql-schedule-default");
        await DatabaseMigrator.ApplyAsync(database.Db, Harness.RepositoryRoot(), CancellationToken.None);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO workers(id,name,roles,max_concurrent_tasks,capabilities,last_seen_at) VALUES('w1','w','[\"worker\"]',1,'[]','2026-01-01 00:00:00.000000Z');"
            + "INSERT INTO worker_processing_settings(worker_id) VALUES('w1')";
        await insert.ExecuteNonQueryAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT schedule_slots FROM worker_processing_settings WHERE worker_id='w1'";
        Assert.Equal(new string('1', 672), (string?)await read.ExecuteScalarAsync());
    }

    /// <summary>Compile chaque instruction sans l'exécuter (<c>EXPLAIN</c>) ; retourne le message d'erreur SQLite, ou null.</summary>
    private static string? Prepare(SqliteConnection connection, string sql)
    {
        foreach (var part in SplitStatements(sql))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN " + part;
            foreach (var name in Parameter.Matches(part).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal))
                command.Parameters.AddWithValue("@" + name, DBNull.Value);
            try
            {
                using var reader = command.ExecuteReader();
                while (reader.Read()) { }
            }
            catch (SqliteException exception) { return exception.Message; }
        }
        return null;
    }

    private static IEnumerable<string> SplitStatements(string sql)
    {
        var current = new StringBuilder();
        char? quote = null;
        foreach (var character in sql)
        {
            if (quote is not null) { if (character == quote) quote = null; }
            else if (character is '\'' or '"') quote = character;
            else if (character == ';')
            {
                if (current.ToString().Trim().Length > 0) yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(character);
        }
        if (current.ToString().Trim().Length > 0) yield return current.ToString();
    }

    private static string Preview(string sql)
    {
        var flat = Regex.Replace(sql, @"\s+", " ").Trim();
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }

    private static List<Statement> ExtractStatements()
    {
        var result = new List<Statement>();
        foreach (var path in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
                     .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            result.AddRange(ExtractStatements(path));
        return result;
    }

    private static List<Statement> ExtractStatements(string path)
    {
        var name = Path.GetFileName(path);
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
        var root = tree.GetCompilationUnitRoot();
        var fields = DeclaredStrings(root);
        var usedFields = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<(string? Field, Statement Statement)>();

        foreach (var node in root.DescendantNodes().OfType<ExpressionSyntax>().Where(IsStringRoot))
        {
            var unresolved = new List<string>();
            var variants = Render(node, name, fields, usedFields, unresolved, 0);
            var line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
            var field = DeclaredName(node);
            foreach (var sql in variants.Where(text => StatementStart.IsMatch(text)))
                candidates.Add((field, new Statement(name, line, sql, unresolved.Distinct().ToArray())));
        }

        // Une constante ou un champ SQL n'est vérifié qu'à travers les requêtes qui l'utilisent : le fragment seul peut être incomplet.
        return candidates.Where(entry => entry.Field is null || !usedFields.Contains(entry.Field)).Select(entry => entry.Statement).ToList();
    }

    private static bool IsStringRoot(ExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.StringLiteralExpression) && !node.IsKind(SyntaxKind.InterpolatedStringExpression)
            && !node.IsKind(SyntaxKind.AddExpression)) return false;
        SyntaxNode? parent = node.Parent;
        while (parent is ParenthesizedExpressionSyntax) parent = parent.Parent;
        if (parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.AddExpression)) return false;
        return parent is not InterpolationSyntax;
    }

    private static string? DeclaredName(ExpressionSyntax node) => node.Parent switch
    {
        EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable } => variable.Identifier.Text,
        ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax property } => property.Identifier.Text,
        _ => null
    };

    private static Dictionary<string, ExpressionSyntax> DeclaredStrings(SyntaxNode root)
    {
        var result = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        foreach (var variable in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            if (variable.Initializer?.Value is { } value && variable.Parent?.Parent is FieldDeclarationSyntax)
                result.TryAdd(variable.Identifier.Text, value);
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            if ((property.ExpressionBody?.Expression ?? property.Initializer?.Value) is { } value)
                result.TryAdd(property.Identifier.Text, value);
        return result;
    }

    /// <summary>Texte SQL possible d'une expression de chaîne (plusieurs variantes pour les conditions), fragments dynamiques résolus.</summary>
    private static List<string> Render(ExpressionSyntax node, string file, Dictionary<string, ExpressionSyntax> fields,
        HashSet<string> usedFields, List<string> unresolved, int depth)
    {
        switch (node)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return [literal.Token.ValueText];
            case ParenthesizedExpressionSyntax parenthesized:
                return Render(parenthesized.Expression, file, fields, usedFields, unresolved, depth);
            case BinaryExpressionSyntax add when add.IsKind(SyntaxKind.AddExpression):
                return Combine(Render(add.Left, file, fields, usedFields, unresolved, depth), Render(add.Right, file, fields, usedFields, unresolved, depth));
            case ConditionalExpressionSyntax conditional:
                return Render(conditional.WhenTrue, file, fields, usedFields, unresolved, depth)
                    .Concat(Render(conditional.WhenFalse, file, fields, usedFields, unresolved, depth)).Distinct().ToList();
            case InterpolatedStringExpressionSyntax interpolated:
                var parts = new List<string> { "" };
                foreach (var content in interpolated.Contents)
                {
                    var piece = content switch
                    {
                        InterpolatedStringTextSyntax text => new List<string> { text.TextToken.ValueText },
                        InterpolationSyntax hole => Render(hole.Expression, file, fields, usedFields, unresolved, depth),
                        _ => new List<string> { "" }
                    };
                    parts = Combine(parts, piece);
                }
                return parts;
            default:
                return Hole(node, file, fields, usedFields, unresolved, depth);
        }
    }

    private static List<string> Hole(ExpressionSyntax node, string file, Dictionary<string, ExpressionSyntax> fields,
        HashSet<string> usedFields, List<string> unresolved, int depth)
    {
        var text = node.ToString().Trim();
        if (text == "Sql.UtcNow") return [Sql.UtcNow];
        if (DynamicFragments.TryGetValue($"{file}::{text}", out var fragment)) return [fragment];
        if (depth < 5 && fields.TryGetValue(text, out var declared))
        {
            usedFields.Add(text);
            return Render(declared, file, fields, usedFields, unresolved, depth + 1);
        }
        unresolved.Add($"{{{text}}}");
        return ["X"];
    }

    private static List<string> Combine(List<string> left, List<string> right) =>
        left.SelectMany(prefix => right.Select(suffix => prefix + suffix)).Distinct().Take(8).ToList();

    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src", "Encoding.Api");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(directory.FullName, "src", "Encoding.Api");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Sources backend introuvables.");
    }
}
