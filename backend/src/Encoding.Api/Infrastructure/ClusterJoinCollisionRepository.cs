using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Cluster;

namespace Encoding.Api.Infrastructure;

public sealed record JoinUserCollision(Guid LocalUserId, string ExpectedUsername, string HostUsername, string ProposedUsername);
public sealed record ResolveJoinUserCollision(string ExpectedUsername, string Username);

public sealed class ClusterJoinCollisionRepository(DbDataSource db)
{
    public async Task<bool> PrepareAsync(ClusterJoinBootstrap bootstrap, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var local = new List<ClusterUserName>();
        await using (var read = Sql.Command("SELECT id,username FROM users ORDER BY id", c, tx))
        await using (var reader = await read.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) local.Add(new(reader.GetGuid(0), reader.GetString(1)));
        // An old host cannot provide an authenticated preflight for initialized installations.
        if (bootstrap.UserNames is null && local.Count > 0) return false;
        var host = bootstrap.UserNames?.Users ?? [];
        await using (var clear = Sql.Command("DELETE FROM cluster_join_user_collisions", c, tx)) await clear.ExecuteNonQueryAsync(ct);
        var collisions = local.Where(user => host.Any(other => other.Id != user.Id && other.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase))).ToArray();
        var reserved = host.Select(user => user.Username).Concat(local.Select(user => user.Username)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var user in collisions)
        {
            var prefix = user.Username[..Math.Min(user.Username.Length, 48)];
            var suggestion = prefix + "-" + user.Id.ToString("N")[..8];
            var suffix = 0;
            while (!reserved.Add(suggestion)) suggestion = prefix + "-" + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await using var insert = Sql.Command("INSERT INTO cluster_join_user_collisions(local_user_id,expected_username,host_username,proposed_username,reserved_names,cluster_id) VALUES(@id,@expected,@host,@proposed,@names,@cluster)",
                c, tx);
            insert.Parameters.AddWithValue("id", user.Id); insert.Parameters.AddWithValue("expected", user.Username);
            insert.Parameters.AddWithValue("host", host.First(other => other.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)).Username);
            insert.Parameters.AddWithValue("proposed", suggestion); insert.Parameters.AddWithValue("names", JsonSerializer.Serialize(host.Select(account => account.Username)));
            insert.Parameters.AddWithValue("cluster", bootstrap.ClusterId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return collisions.Length == 0;
    }

    public async Task<IReadOnlyList<JoinUserCollision>> ListAsync(CancellationToken ct)
    {
        var result = new List<JoinUserCollision>();
        await using var command = db.CreateCommand("SELECT local_user_id,expected_username,host_username,proposed_username FROM cluster_join_user_collisions ORDER BY expected_username");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    public async Task<bool> ResolveAsync(Guid id, ResolveJoinUserCollision request, CancellationToken ct)
    {
        if (!AuthService.IsValidUsername(request.Username)) return false;
        var username = request.Username.Trim();
        await using var c = await db.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using var read = Sql.Command("SELECT expected_username,reserved_names FROM cluster_join_user_collisions WHERE local_user_id=@id", c, tx);
        read.Parameters.AddWithValue("id", id);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(0) != request.ExpectedUsername) return false;
        var reserved = JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [];
        await reader.DisposeAsync();
        if (reserved.Contains(username, StringComparer.OrdinalIgnoreCase)) return false;
        await using var update = Sql.Command("UPDATE users SET username=@name,updated_at=clock_timestamp() WHERE id=@id AND username=@expected AND NOT EXISTS(SELECT 1 FROM users WHERE lower(username)=lower(@name) AND id<>@id)",
            c, tx);
        update.Parameters.AddWithValue("id", id); update.Parameters.AddWithValue("name", username); update.Parameters.AddWithValue("expected", request.ExpectedUsername);
        if (await update.ExecuteNonQueryAsync(ct) != 1) return false;
        await using var clear = Sql.Command("DELETE FROM cluster_join_user_collisions WHERE local_user_id=@id", c, tx);
        clear.Parameters.AddWithValue("id", id); await clear.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
