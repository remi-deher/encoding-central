using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime;

public sealed class RuntimeClient : IDisposable
{
    private readonly HttpClient http=new() { Timeout=Timeout.InfiniteTimeSpan };
    private readonly WorkerIdentityState identity;
    public RuntimeClient(WorkerIdentityState identity, string? apiBaseUrl = null)
    {
        this.identity=identity;
        ApiBaseUrl=apiBaseUrl ?? identity.ApiBaseUrl!;
        http.BaseAddress=new Uri(ApiBaseUrl);
        http.DefaultRequestHeaders.Add("X-Node-Token",identity.NodeToken);
    }
    public Guid NodeId=>identity.Id;
    public string ApiBaseUrl { get; }
    private string Route(string path)=>$"api/v1/runtime/nodes/{identity.Id:D}/{path}";
    public async Task<T?> PostAsync<T>(string path,object body,CancellationToken ct)
    {
        using var response=await http.PostAsJsonAsync(Route(path),body,AgentJson.Options,ct);
        if(!response.IsSuccessStatusCode)
        {
            var content=await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Content: {content}", null, response.StatusCode);
        }
        return response.StatusCode==HttpStatusCode.NoContent?default:await response.Content.ReadFromJsonAsync<T>(AgentJson.Options,ct);
    }
    public async Task<T> GetAsync<T>(string path,CancellationToken ct)
    {
        using var response=await http.GetAsync(Route(path),ct);
        if(!response.IsSuccessStatusCode)
        {
            var content=await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Content: {content}", null, response.StatusCode);
        }
        return (await response.Content.ReadFromJsonAsync<T>(AgentJson.Options,ct))!;
    }
    public Task<RuntimePublicationState> PublicationStateAsync(Guid id,long epoch,CancellationToken ct)=>GetAsync<RuntimePublicationState>($"executions/{id:D}/publication?epoch={epoch}",ct);
    public Task ReportSourceOperationAsync(Guid id,long epoch,string stepNodeId,Guid invocationId,RuntimeCommandLog operation,CancellationToken ct)=>
        PostAsync<object>($"executions/{id:D}/operation-logs",new RuntimeFence(epoch,[new RuntimeStepCommand(stepNodeId,invocationId,operation)]),ct);
    public async Task<ExecutionResponse> ExecutionAsync(Guid id,CancellationToken ct)=>(await http.GetFromJsonAsync<ExecutionResponse>($"api/v1/executions/{id:D}",AgentJson.Options,ct))!;
    public async Task PutAsync(Guid id,long epoch,string file,string sha,bool original,CancellationToken ct,string? inputPath=null)
    {
        var operationId=Guid.NewGuid(); var startedAt=DateTimeOffset.UtcNow; var bytes=new FileInfo(file).Length;
        RuntimeCommandCapture.RecordOperation("Téléverser un artefact vers le serveur",operationId,startedAt,null,null,bytes);
        try
        {
            await using var stream=File.OpenRead(file); using var content=new StreamContent(stream);
            using var response=await http.PutAsync(Route($"executions/{id:D}/dav/{sha}?epoch={epoch}&original={original.ToString().ToLowerInvariant()}{(inputPath is null?string.Empty:"&inputPath="+Uri.EscapeDataString(inputPath))}"),
                content,ct);
            if(!response.IsSuccessStatusCode)
            {
                var err=await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Content: {err}", null, response.StatusCode);
            }
            RuntimeCommandCapture.RecordOperation("Téléverser un artefact vers le serveur",operationId,startedAt,DateTimeOffset.UtcNow,0,bytes);
        }
        catch { RuntimeCommandCapture.RecordOperation("Téléverser un artefact vers le serveur",operationId,startedAt,DateTimeOffset.UtcNow,1,bytes); throw; }
    }
    public async Task<long> DownloadAsync(Guid id,long epoch,string sha,string target,CancellationToken ct)
    {
        var operationId=Guid.NewGuid(); var startedAt=DateTimeOffset.UtcNow;
        RuntimeCommandCapture.RecordOperation("Télécharger un artefact depuis le serveur",operationId,startedAt,null,null,null);
        try
        {
        if(File.Exists(target) && new FileInfo(target).Length > 0)
        {
            var existingBytes=new FileInfo(target).Length;
            RuntimeCommandCapture.RecordOperation("Télécharger un artefact depuis le serveur",operationId,startedAt,DateTimeOffset.UtcNow,0,existingBytes);
            return existingBytes;
        }
        var partial=target+".partial"; var offset=File.Exists(partial)?new FileInfo(partial).Length:0;
        using var request=new HttpRequestMessage(HttpMethod.Get,Route($"executions/{id:D}/dav/{sha}?epoch={epoch}"));
        if(offset>0) request.Headers.Range=new RangeHeaderValue(offset,null);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(response.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); throw new IOException("Le téléchargement partiel doit être recommencé."); }
        if(!response.IsSuccessStatusCode)
        {
            var content=await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Content: {content}", null, response.StatusCode);
        }
        await using(var file=new FileStream(partial,response.StatusCode==HttpStatusCode.PartialContent?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
        { await response.Content.CopyToAsync(file,ct); await file.FlushAsync(ct); file.Flush(true); }
        if(new FileInfo(partial).Length==0) { File.Delete(partial); throw new IOException("Le téléchargement est incomplet ou vide."); }
        File.Move(partial,target,true);
        var bytes=new FileInfo(target).Length;
        RuntimeCommandCapture.RecordOperation("Télécharger un artefact depuis le serveur",operationId,startedAt,DateTimeOffset.UtcNow,0,bytes);
        return bytes;
        }
        catch { RuntimeCommandCapture.RecordOperation("Télécharger un artefact depuis le serveur",operationId,startedAt,DateTimeOffset.UtcNow,1,null); throw; }
    }
    public Task<Encoding.Contracts.Flows.ResourceVersionInfo> ResourceVersionAsync(Encoding.Contracts.Flows.ResourceReference reference,CancellationToken ct)=>
        GetAsync<Encoding.Contracts.Flows.ResourceVersionInfo>($"resources/{reference.Id:D}/versions/{reference.Version}",ct);
    public Task<IReadOnlyList<Encoding.Contracts.Flows.ResourceVersionInfo>> LatestResourceVersionsAsync(CancellationToken ct)=>
        GetAsync<IReadOnlyList<Encoding.Contracts.Flows.ResourceVersionInfo>>("resources",ct);

    /// <summary>
    /// Met les fichiers d'une version de ressource dans le cache du worker (<paramref name="cacheRoot"/>, un fichier par empreinte) après
    /// contrôle de taille et d'empreinte ; déjà présents, ils ne sont pas retéléchargés. Avec <paramref name="runFolder"/>, ils y sont copiés sous leur nom.
    /// </summary>
    public async Task EnsureResourceAsync(Encoding.Contracts.Flows.ResourceVersionInfo info,string cacheRoot,string? runFolder,CancellationToken ct)
    {
        Directory.CreateDirectory(cacheRoot);
        if(runFolder is not null) Directory.CreateDirectory(runFolder);
        foreach(var file in info.Files)
        {
            if(file.Name!=Path.GetFileName(file.Name)||file.Name.Length==0||!System.Text.RegularExpressions.Regex.IsMatch(file.Sha256,
                "^[0-9a-f]{64}$")) throw new IOException("Fichier de ressource invalide.");
            var cached=Path.Combine(cacheRoot,file.Sha256);
            if(!(File.Exists(cached)&&new FileInfo(cached).Length==file.Bytes&&await SafeMediaPaths.DigestAsync(cached,ct)==file.Sha256))
            {
                var partial=cached+"."+Guid.NewGuid().ToString("N")+".partial";
                try
                {
                    using var response=await http.GetAsync(Route($"resources/blobs/{file.Sha256}"),HttpCompletionOption.ResponseHeadersRead,ct);
                    if(!response.IsSuccessStatusCode) throw new HttpRequestException($"Le fichier « {file.Name} » de la ressource est indisponible ({(int)response.StatusCode}).",
                        null,response.StatusCode);
                    await using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
                    { await response.Content.CopyToAsync(output,ct); await output.FlushAsync(ct); output.Flush(true); }
                    if(new FileInfo(partial).Length!=file.Bytes||await SafeMediaPaths.DigestAsync(partial,
                        ct)!=file.Sha256) throw new IOException($"Le fichier « {file.Name} » est incomplet ou corrompu.");
                    File.Move(partial,cached,true);
                }
                finally { if(File.Exists(partial)) File.Delete(partial); }
            }
            if(runFolder is not null) File.Copy(cached,Path.Combine(runFolder,file.Name),true);
        }
    }
    public void Dispose()=>http.Dispose();
}
