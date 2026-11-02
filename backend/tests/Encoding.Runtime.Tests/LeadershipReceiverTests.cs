using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Encoding.Contracts.Cluster;
using Encoding.NodeAgent;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Encoding.Runtime.Tests;

public sealed class LeadershipReceiverTests : IDisposable
{
    private readonly string testDir;

    public LeadershipReceiverTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), "encoding_receiver_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDir))
        {
            try { Directory.Delete(testDir, recursive: true); }
            catch { }
        }
    }

    private NodeAgentOptions CreateOptions() =>
        NodeAgentOptions.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Node:Name"] = "test-worker",
                ["Node:DataDirectory"] = testDir,
                ["Node:Roles"] = "worker"
            }).Build());

    [Fact]
    public async Task EmergencyPromoteAsync_FailsCleanlyWhenNoBackupExists()
    {
        var options = CreateOptions();

        var service = new LeadershipReceiverService(options, NullLogger<LeadershipReceiverService>.Instance);
        var result = await service.EmergencyPromoteAsync(force: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Aucune sauvegarde", result.Message);
        Assert.False(File.Exists(service.DatabaseFilePath));
    }

    [Fact]
    public async Task EmergencyPromoteAsync_RestoresDatabaseFromBackup()
    {
        var options = CreateOptions();

        var service = new LeadershipReceiverService(options, NullLogger<LeadershipReceiverService>.Instance);

        // Créer un faux snapshot dans backup/cluster-latest.db
        Directory.CreateDirectory(service.BackupDirectory);
        var fakeDbContent = "SQLITE_HEADER_TEST_" + Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(service.BackupFilePath, fakeDbContent);

        var info = new LocalBackupInfo("digest123", fakeDbContent.Length, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(service.BackupInfoFilePath, JsonSerializer.Serialize(info));

        // Créer d'anciens fichiers résiduels WAL et SHM
        var walPath = service.DatabaseFilePath + "-wal";
        var shmPath = service.DatabaseFilePath + "-shm";
        await File.WriteAllTextAsync(walPath, "stale wal");
        await File.WriteAllTextAsync(shmPath, "stale shm");

        var result = await service.EmergencyPromoteAsync(force: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(service.DatabaseFilePath));
        Assert.Equal(fakeDbContent, await File.ReadAllTextAsync(service.DatabaseFilePath));

        // Les fichiers WAL et SHM résiduels doivent avoir été purgés
        Assert.False(File.Exists(walPath));
        Assert.False(File.Exists(shmPath));
    }

    [Fact]
    public async Task GetLocalBackupInfoAsync_ReturnsNullWhenFileDoesNotExist()
    {
        var options = CreateOptions();

        var service = new LeadershipReceiverService(options, NullLogger<LeadershipReceiverService>.Instance);
        var info = await service.GetLocalBackupInfoAsync(CancellationToken.None);

        Assert.Null(info);
    }

    [Fact]
    public async Task SyncKeysAsync_DownloadsAndPersistsKeyFiles()
    {
        var options = CreateOptions();
        var keyContent = "<key id=\"c296aec3-2570-47e3-84ff-d0be5a1458f4\"><test/></key>";
        var bundle = new LeadershipKeyBundle([new LeadershipKeyEntry("key-c296aec3-2570-47e3-84ff-d0be5a1458f4.xml", keyContent)]);

        var fakeHandler = new FakeHttpMessageHandler((req) =>
        {
            Assert.Equal("/api/v1/cluster/leadership/keys", req.RequestUri?.AbsolutePath);
            Assert.Equal("test-token", req.Headers.GetValues("X-Node-Token").First());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(bundle)
            };
        });

        using var client = new HttpClient(fakeHandler) { BaseAddress = new Uri("http://master:8090") };
        var service = new LeadershipReceiverService(options, NullLogger<LeadershipReceiverService>.Instance, httpClient: client);

        await service.SyncKeysAsync("http://master:8090", "test-token", CancellationToken.None);

        var savedPath = Path.Combine(service.PluginKeysDirectory, "key-c296aec3-2570-47e3-84ff-d0be5a1458f4.xml");
        Assert.True(File.Exists(savedPath));
        Assert.Equal(keyContent, await File.ReadAllTextAsync(savedPath));
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
