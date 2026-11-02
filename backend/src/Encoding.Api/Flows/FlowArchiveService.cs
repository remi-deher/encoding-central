using System.IO.Compression;
using System.Text.Json;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

public sealed class FlowArchiveService(FlowRepository repository, FlowDefinitionValidator validator)
{
    public async Task<byte[]?> ExportAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count is < 1 or > 100) return null;
        using var stream=new MemoryStream(); long expanded=0; int documents=0;
        using (var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
        foreach(var id in ids.Distinct().Order())
        {
            var versions=await repository.VersionsAsync(id,ct);
            var draft=await repository.DraftAsync(id,ct);
            if(versions.Count==0 && draft is null) return null;
            if(draft is not null) {
                var bytes=JsonSerializer.SerializeToUtf8Bytes(new {kind="draft",draftRevision=draft.DraftRevision,definition=draft.Definition},FlowRepository.JsonOptions); expanded+=bytes.Length;
                if(expanded>20*1024*1024 || ++documents>1000) throw new InvalidDataException("L’archive dépasse la limite configurée.");
                await using var entry=zip.CreateEntry($"{id:D}/draft.json",CompressionLevel.Fastest).Open(); await entry.WriteAsync(bytes,ct);
            }
            foreach(var definition in versions)
            {
                var bytes=JsonSerializer.SerializeToUtf8Bytes(definition,FlowRepository.JsonOptions); expanded+=bytes.Length;
                if(expanded>20*1024*1024 || ++documents>1000) throw new InvalidDataException("La sélection dépasse la limite d’une archive de restauration.");
                await using var entry=zip.CreateEntry($"{id:D}/v{definition.Version}.json",CompressionLevel.Fastest).Open();
                await entry.WriteAsync(bytes,ct);
                if(stream.Length>20*1024*1024) throw new InvalidDataException("L’export dépasse 20 Mo.");
            }
        }
        return stream.ToArray();
    }

    public async Task<(FlowValidationResult Result, bool Conflict, int Count)> ImportAsync(Stream stream, bool zip, CancellationToken ct)
    {
        const int limit=20*1024*1024;
        using var buffer=new MemoryStream();
        var block=new byte[81920]; int read;
        while((read=await stream.ReadAsync(block,ct))>0) { if(buffer.Length+read>limit) throw new InvalidDataException("L’import dépasse 20 Mo."); await buffer.WriteAsync(block.AsMemory(0,read),ct); }
        buffer.Position=0;
        var definitions=new List<FlowDefinition>(); var drafts=new List<FlowDraftImport>();
        var issues=new List<FlowValidationIssue>();
        long expanded=0;
        async Task ReadAsync(Stream input,string name)
        {
            using var bytes=new MemoryStream();
            while((read=await input.ReadAsync(block,ct))>0)
            {
                expanded+=read;
                if(expanded>limit || bytes.Length+read>1048576+4096) throw new InvalidDataException("Limite des documents décompressés dépassée.");
                await bytes.WriteAsync(block.AsMemory(0,read),ct);
            }
            using var document=JsonDocument.Parse(bytes.ToArray());
            if(document.RootElement.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("Un document de flow doit être un objet JSON.");
            if(document.RootElement.TryGetProperty("kind",out var kind)) {
                if(kind.ValueKind!=JsonValueKind.String || kind.GetString()!="draft" || !document.RootElement.TryGetProperty("definition",out var body)
                    || document.RootElement.EnumerateObject().Any(p=>p.Name is not("kind" or "definition" or "draftRevision"))) throw new InvalidDataException("Enveloppe de brouillon invalide.");
                var (definition,_)=validator.ValidateDocument(body);
                if(definition is null) throw new InvalidDataException("Brouillon absent.");
                var result=validator.ValidateDraft(definition);
                issues.AddRange(result.Issues.Select(issue=>issue with {Path=name+issue.Path}));
                var revision=document.RootElement.TryGetProperty("draftRevision",out var value) ? value.ValueKind==JsonValueKind.Number && value.TryGetInt32(out var number)?number:0 : 1;
                if(revision<1 || revision==int.MaxValue) throw new InvalidDataException("Révision de brouillon invalide.");
                if(result.IsValid) drafts.Add(new(definition,revision));
            } else {
                var (definition,result)=validator.ValidateDocument(document.RootElement);
                issues.AddRange(result.Issues.Select(issue=>issue with { Path=name+issue.Path }));
                if(result.IsValid) definitions.Add(definition!);
            }
        }
        if(zip)
        {
            using var archive=new ZipArchive(buffer,ZipArchiveMode.Read);
            if(archive.Entries.Count is <1 or >1000) throw new InvalidDataException("Une archive contient de 1 à 1000 documents JSON.");
            foreach(var entry in archive.Entries)
            {
                if(entry.FullName.EndsWith('/')) continue;
                if(!entry.Name.EndsWith(".json",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("L’archive doit contenir uniquement des documents JSON.");
                // Never extract archive paths onto the filesystem.
                await using var input=entry.Open(); await ReadAsync(input,entry.FullName);
            }
        }
        else await ReadAsync(buffer,"document");
        if(definitions.Count==0 && drafts.Count==0 && issues.Count==0) issues.Add(new("archive.empty","/","Aucun flow à importer."));
        if(definitions.GroupBy(d=>(d.Id,d.Version)).Any(g=>g.Count()>1)) issues.Add(new("archive.duplicate","/","Une version est présente plusieurs fois dans l’archive."));
        if(drafts.GroupBy(d=>d.Definition.Id).Any(g=>g.Count()>1)) issues.Add(new("archive.draft.duplicate","/","Un brouillon est présent plusieurs fois."));
        if(issues.Count>0) return(new(issues),false,0);
        var saved=await repository.SaveManyAsync(definitions.Select(d=>new SaveFlowRequest(d.Version-1,d)).ToArray(),true,ct,drafts);
        return(new([]),!saved,saved?definitions.Select(d=>d.Id).Concat(drafts.Select(d=>d.Definition.Id)).Distinct().Count():0);
    }
}
