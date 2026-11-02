using Encoding.Api.Infrastructure.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Encoding.Api.Executions;

public sealed class RuntimeArtifactStore(DbDataSource dataSource,IConfiguration configuration)
{
    private readonly string root=Path.GetFullPath(configuration["Runtime:ArtifactDirectory"]
        ??(configuration["Node:DataDirectory"] is { Length: > 0 } dataDir ? Path.Combine(dataDir,"runtime-artifacts") : Path.Combine(AppContext.BaseDirectory,"runtime-artifacts")));
    public string Root=>root;
    public string DirectoryFor(Guid id)=>Path.Combine(root,id.ToString("D"));
    public static bool ValidDigest(string digest)=>Regex.IsMatch(digest??"","^[0-9a-f]{64}$",RegexOptions.CultureInvariant);
    public sealed record ProducedArtifact(string Sha256,string Name,long Bytes);
    public async Task<IReadOnlyList<ProducedArtifact>> ProducedAsync(Guid id,CancellationToken ct)
    {
        await using var command=dataSource.CreateCommand("""
            SELECT DISTINCT item.value->>'sha256',item.value->>'name',a.byte_length
            FROM execution_steps s
            CROSS JOIN LATERAL jsonb_each(COALESCE(s.checkpoint->'context'->'variables','{}'::jsonb)) item
            JOIN execution_artifacts a ON a.execution_id=s.execution_id AND a.sha256=item.value->>'sha256'
            WHERE s.execution_id=@id AND s.completed_at IS NOT NULL AND item.key LIKE 'artifact.%'
            ORDER BY 2,1
            """);
        command.Parameters.AddWithValue("id",id);
        await using var reader=await command.ExecuteReaderAsync(ct);
        var result=new List<ProducedArtifact>();
        while(await reader.ReadAsync(ct)) result.Add(new(reader.GetString(0),Path.GetFileName(reader.GetString(1)),reader.GetInt64(2)));
        return result;
    }
    public string PathFor(Guid id,string digest)
    {
        if(!ValidDigest(digest)) throw new ArgumentException("Empreinte SHA-256 invalide.");
        return Path.Combine(root,id.ToString("D"),digest);
    }
    internal async Task<bool> IsCachedAsync(Guid id,string digest,long bytes,CancellationToken ct)
    {
        var path=PathFor(id,digest);if(!File.Exists(path)||new FileInfo(path).Length!=bytes) return false;
        await using var file=File.OpenRead(path);return Convert.ToHexStringLower(await SHA256.HashDataAsync(file,ct))==digest;
    }
    /// <summary>Stores a peer checkpoint blob without granting any task lease or publication permission.</summary>
    internal async Task CacheAsync(Guid id,string digest,long expectedBytes,Stream input,CancellationToken ct)
    {
        if(!ValidDigest(digest)||expectedBytes<=0||expectedBytes>configuration.GetValue<long>("Runtime:MaxArtifactBytes",1024L*1024*1024*1024)) throw new InvalidDataException("Artefact invalide.");
        var target=PathFor(id,digest);Directory.CreateDirectory(DirectoryFor(id));var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            long length=0;using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
            {
                var buffer=new byte[81920];int count;
                while((count=await input.ReadAsync(buffer,ct))>0) {length+=count;if(length>expectedBytes) throw new InvalidDataException("Artefact trop long.");
                    hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),ct);}
                await output.FlushAsync(ct);output.Flush(true);
            }
            if(length!=expectedBytes||Convert.ToHexStringLower(hash.GetHashAndReset())!=digest) throw new InvalidDataException("Artefact incomplet ou corrompu.");
            if(!File.Exists(target)) {try {Encoding.Core.AtomicFilePublication.Publish(temporary,target);} catch(IOException) when(File.Exists(target)) {}}
            await using(var existing=File.OpenRead(target)) if(Convert.ToHexStringLower(await SHA256.HashDataAsync(existing,ct))!=digest) throw new InvalidDataException("Cache durable corrompu.");
            Encoding.Core.AtomicFilePublication.Confirm(target);
        }
        finally {if(File.Exists(temporary)) File.Delete(temporary);}
    }
    public async Task<bool> AuthorizeAsync(Guid id,Guid actor,long epoch,CancellationToken ct)
    {
        await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
        return await RuntimeRepository.FenceAsync(id,actor,epoch,c,tx,ct) || await RuntimeRepository.FenceAsync(id,actor,epoch,c,tx,ct,true);
    }
    public async Task<bool> PutAsync(Guid id,Guid actor,long epoch,string digest,Stream source,bool original,CancellationToken ct,string? inputPath=null)
    {
        if(!await AuthorizeAsync(id,actor,epoch,ct)) return false;
        var target=PathFor(id,digest); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            long length=0; using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
            {
                var buffer=new byte[81920]; int count;
                while((count=await source.ReadAsync(buffer,ct))>0)
                {
                    length+=count; if(length>configuration.GetValue<long>("Runtime:MaxArtifactBytes",1024L*1024*1024*1024)) throw new IOException("L’artefact dépasse la limite configurée.");
                    hash.AppendData(buffer,0,count); await output.WriteAsync(buffer.AsMemory(0,count),ct);
                }
                await output.FlushAsync(ct); output.Flush(true);
            }
            if(length==0) throw new InvalidDataException("Le transfert est vide.");
            var streamSha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if(!original && streamSha!=digest) throw new InvalidDataException("Le transfert est incomplet ou son empreinte est incorrecte.");
            await using var c=await dataSource.OpenConnectionAsync(ct); await using var tx=await c.BeginTransactionAsync(ct);
            if(!await RuntimeRepository.FenceAsync(id,actor,epoch,c,tx,ct) && !await RuntimeRepository.FenceAsync(id,actor,epoch,c,tx,ct,true)) return false;
            if(original)
            {
                await using var set=Sql.Command("UPDATE execution_work SET source_sha=@sha WHERE execution_id=@id AND source_node_id=@actor AND (source_sha IS NULL OR source_sha=@sha)",c,tx);
                set.Parameters.AddWithValue("id",id); set.Parameters.AddWithValue("actor",actor); set.Parameters.AddWithValue("sha",digest);
                if(await set.ExecuteNonQueryAsync(ct)!=1) return false;
            }
            if(!File.Exists(target))
            {
                try { Encoding.Core.AtomicFilePublication.Publish(temporary,target); }
                catch(IOException) when(File.Exists(target)) { }
            }
            if(!original)
            {
                await using(var existing=File.OpenRead(target))
                    if(Convert.ToHexStringLower(await SHA256.HashDataAsync(existing,ct))!=digest) throw new IOException("L’artefact durable existant est corrompu.");
            }
            Encoding.Core.AtomicFilePublication.Confirm(target);
            await using var register=Sql.Command("INSERT INTO execution_artifacts(execution_id,sha256,byte_length) VALUES(@id,@sha,@length) ON CONFLICT DO NOTHING",c,tx);
            register.Parameters.AddWithValue("id",id); register.Parameters.AddWithValue("sha",digest); register.Parameters.AddWithValue("length",length);
            await register.ExecuteNonQueryAsync(ct);
            if(inputPath is not null)
            {
                if (inputPath.StartsWith(Encoding.Contracts.Flows.FlowArtifactReference.Prefix, StringComparison.Ordinal)) return false;
                inputPath=RuntimeRepository.NormalizePath(inputPath);
                await using var declared=Sql.Command("SELECT EXISTS(SELECT 1 FROM executions e JOIN execution_work w ON w.execution_id=e.id, json_each(e.flow_definition->'nodes') n WHERE e.id=@id AND w.source_node_id=@actor AND replace(n.value->'configuration'->>'sourceFile',char(92),'/')=@path)",
                    c,tx);
                declared.Parameters.AddWithValue("id",id); declared.Parameters.AddWithValue("actor",actor); declared.Parameters.AddWithValue("path",inputPath);
                if(!(await declared.ScalarBoolAsync(ct))) return false;
                await using var bind=Sql.Command("INSERT INTO execution_inputs(execution_id,input_path,sha256) VALUES(@id,@path,@sha) ON CONFLICT(execution_id,input_path) DO UPDATE SET sha256=EXCLUDED.sha256 WHERE execution_inputs.sha256=EXCLUDED.sha256 RETURNING sha256",
                    c,tx);
                bind.Parameters.AddWithValue("id",id); bind.Parameters.AddWithValue("path",inputPath); bind.Parameters.AddWithValue("sha",digest);
                if(await bind.ExecuteScalarAsync(ct) is null) return false;
            }
            await tx.CommitAsync(ct); return true;
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
}
