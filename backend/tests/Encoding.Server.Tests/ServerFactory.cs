using Encoding.Server.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Encoding.Server.Tests;

public sealed class ServerFactory : WebApplicationFactory<Program>
{
    private readonly string _directory = Directory.CreateTempSubdirectory("encoding-central-").FullName;

    public string DatabasePath => Path.Combine(_directory, "server.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Path", DatabasePath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }
}
