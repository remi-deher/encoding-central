using System.Data.Common;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Data.Sqlite;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Base SQLite isolée d'un scénario : un fichier temporaire par fixture (architecture 0043).
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public static bool IsSqlite => true;
    public static string? ConfiguredConnection => "sqlite";

    private readonly string? file;
    private readonly List<DbDataSource> opened = [];

    /// <summary>Chaîne de connexion « Cluster » de la base isolée.</summary>
    public string ConnectionString { get; }
    public string Provider => DatabaseProvider.SqliteName;
    public DbDataSource Db { get; }

    private TestDatabase(string connectionString, DbDataSource db, string? file)
    { ConnectionString = connectionString; Db = db; this.file = file; }

    public static Task<TestDatabase> CreateAsync(string? _, string prefix) => CreateSqliteAsync(prefix);

    public static Task<TestDatabase> CreateSqliteAsync(string prefix)
    {
        var name = prefix + "_" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), name + ".db");
        var connection = $"Data Source={path};Pooling=False";
        return Task.FromResult(new TestDatabase(connection, new SqliteDataSource(connection), path));
    }

    /// <summary>Second accès à la même base, comme un second serveur du cluster.</summary>
    public DbDataSource OpenAnother()
    {
        DbDataSource source = new SqliteDataSource(ConnectionString);
        opened.Add(source);
        return source;
    }

    /// <summary>Réglages à ajouter à la configuration d'une application de test.</summary>
    public IEnumerable<KeyValuePair<string, string>> Settings() =>
        [new("ConnectionStrings:Cluster", ConnectionString)];

    public async ValueTask DisposeAsync()
    {
        foreach (var source in opened) await source.DisposeAsync();
        await Db.DisposeAsync();
        if (file is not null)
            foreach (var path in new[] { file, file + "-wal", file + "-shm" }) if (File.Exists(path)) File.Delete(path);
    }
}
