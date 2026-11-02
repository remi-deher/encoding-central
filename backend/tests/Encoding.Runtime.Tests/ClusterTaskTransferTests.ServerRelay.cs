using Encoding.Api.Infrastructure.Data;
using System.Text.Json;
using Encoding.Api.Executions;
using Encoding.Contracts.Executions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Relais en mode « server » demandé par le flow lui-même (nodes-handoff), complété par un worker : le serveur
/// doit accepter l'étape quand la source est transférable, et ne jamais confondre « cible = moi » avec une erreur de cluster.
/// </summary>
public sealed partial class ClusterTaskTransferTests
{
    private static JsonElement Scalar(string value)=>JsonSerializer.SerializeToElement(value);

    /// <summary>Joue input puis le bloc de relais jusqu'à la complétion du relais ; renvoie le résultat de la complétion.</summary>
    private static async Task<bool> CompleteServerRelayAsync(Site site,Guid target,bool sourceCopied)
    {
        var(registry,_,_)=Harness.Services();
        var relay=Harness.Node(registry,"relay","nodes-handoff",new(){["mode"]=Scalar("server"),["targetServerPeerId"]=Scalar(target.ToString("D"))});
        var flow=Harness.Chain(Harness.Node(registry,"input","base-input-video"),relay,Harness.Node(registry,"end","base-complete-flow"));
        var id=await site.EnqueueAsync(definition:flow);
        // La source est enregistrée comme artefact transférable : c'est le cas d'un worker qui n'a pas l'accès direct, ou d'une source déjà préparée.
        var digest="";
        if(sourceCopied)
        {
            var bytes=new byte[]{1,2,3,4};digest=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            Directory.CreateDirectory(site.Artifacts.DirectoryFor(id));await File.WriteAllBytesAsync(site.Artifacts.PathFor(id,digest),bytes);
            await using var register=site.Db.Db.CreateCommand("INSERT INTO execution_artifacts(execution_id,sha256,byte_length) VALUES(@id,@sha,4)");
            register.Parameters.AddWithValue("id",id);register.Parameters.AddWithValue("sha",digest);await register.ExecuteNonQueryAsync();
        }
        var lease=await site.Runtime.ClaimAsync(site.Node,default)??throw new InvalidOperationException("Le worker doit prendre l'exécution.");
        var input=await site.Runtime.StartStepAsync(id,site.Node,new(lease.Epoch,"input"),default)??throw new InvalidOperationException($"Étape d'entrée refusée : époque {lease.Epoch}, exécution {lease.ExecutionId==id}, état {(await new ExecutionRepository(site.Db.Db).GetAsync(id,default))?.State}.");
        var metadata=JsonSerializer.SerializeToElement(new{videoCodec="hevc"});
        var context=new RuntimeContext(digest,".mkv",new Dictionary<string,JsonElement>(),metadata,metadata,[]);
        Assert.True(await site.Runtime.CompleteStepAsync(id,site.Node,new(lease.Epoch,Guid.NewGuid(),lease.Revision,new("input",input.InvocationId,"success","relay",context,null)),default));
        var started=await site.Runtime.StartStepAsync(id,site.Node,new(lease.Epoch,"relay"),default)??throw new InvalidOperationException("Étape de relais refusée au démarrage.");
        var revision=(await new ExecutionRepository(site.Db.Db).GetAsync(id,default))!.Revision;
        return await site.Runtime.CompleteStepAsync(id,site.Node,new(lease.Epoch,Guid.NewGuid(),revision,new("relay",started.InvocationId,"success","end",context,null,Handoff:new("server",target))),default);
    }

    [Fact]
    public async Task ServerRelayToAnotherServerIsAcceptedWhenTheSourceIsTransferable()
    {
        await using var pair=await Pair.CreateAsync();
        Assert.True(await CompleteServerRelayAsync(pair.A,pair.B.Identity.PeerId,sourceCopied:true));
    }

    [Fact]
    public async Task ServerRelayToTheLocalServerIsNeverAClusterRefusal()
    {
        await using var pair=await Pair.CreateAsync();
        // Cible = serveur propriétaire : rien à céder entre serveurs. L'étape est acceptée et le travail est confié à un autre worker éligible du même serveur.
        Assert.True(await CompleteServerRelayAsync(pair.A,pair.A.Identity.PeerId,sourceCopied:true));
        await using var read=pair.A.Db.Db.CreateCommand("SELECT handoff_selector->>'mode' FROM execution_work WHERE handoff_pending");
        Assert.Equal("eligible",(string?)await read.ExecuteScalarAsync());
    }
}
