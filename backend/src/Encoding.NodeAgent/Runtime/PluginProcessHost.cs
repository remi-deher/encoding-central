using System.Collections.Concurrent;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Plugins;
using Encoding.Core.Plugins;

namespace Encoding.NodeAgent.Runtime;

public sealed class PluginLaunch
{
    public bool Enabled { get; set; } = true;
    public string Id { get; set; }="";
    public string Version { get; set; }="";
    public string Executable { get; set; }="";
    public string[] Arguments { get; set; }=[];
    public string[] ReadOnlyPaths { get; set; }=[];
    public bool IsNative { get; set; }
}

/// <summary>Third party implementations run as separate child processes with a versioned JSON envelope.</summary>
public sealed class PluginProcessHost(IProcessToolRunner processes,IConfiguration configuration,NodeAgentOptions options)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private const int MaxVariableChanges=64, MaxVariableNameLength=128, MaxVariableBytes=256*1024, MaxDetailLength=4096, MaxErrorLength=400, MaxFingerprintedFiles=5000;
    private readonly ConcurrentDictionary<string,(string Fingerprint,PluginManifest Manifest)> described=new(StringComparer.Ordinal);
    /// <summary>Plugins déclarés dans la configuration du worker, puis plugins du Store téléchargés et vérifiés (la configuration prime).</summary>
    public IReadOnlyList<PluginLaunch> Installed
    {
        get
        {
            var declared=configuration.GetSection("Runtime:Plugins").Get<PluginLaunch[]>()??[];
            var downloaded=CustomPluginCatalog.Scan(CustomPluginCatalog.DirectoryFor(configuration)).Where(plugin=>declared.All(item=>item.Id!=plugin.Id || item.Version!=plugin.Version));
            return declared.Concat(downloaded)
                .Where(plugin => plugin.Enabled).DistinctBy(plugin => (plugin.Id, plugin.Version)).ToList();
        }
    }

    /// <summary>
    /// Manifeste du plugin installé. Le résultat est conservé tant que les fichiers du plugin ne changent pas (chemin, taille,
    /// date) : sans cela, chaque bloc et chaque rafraîchissement des capacités lançaient un processus sous Landlock de plus.
    /// </summary>
    public async Task<PluginManifest> DescribeAsync(PluginLaunch plugin,CancellationToken ct)
    {
        ValidatePaths(plugin);
        var key=plugin.Id+"@"+plugin.Version;
        var fingerprint=Fingerprint(plugin);
        if(described.TryGetValue(key,out var cached) && cached.Fingerprint==fingerprint) return cached.Manifest;
        PluginManifest manifest;
        if (plugin.IsNative)
        {
            var packageDirectory=plugin.ReadOnlyPaths.Single();
            manifest=JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(packageDirectory,PluginPackage.ManifestFile)),Json)
                ??throw new IOException("Manifeste du plugin natif absent.");
        }
        else
        {
            var directory=Path.Combine(options.DataDirectory,"plugin-describe",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))));
            Directory.CreateDirectory(directory);
            var result=await processes.RunAsync(plugin.Executable,plugin.Arguments,directory,TimeSpan.FromSeconds(20),ct,
                "{\"type\":\"describe\",\"contractVersion\":1}\n",sandbox:true,readOnlyPaths:plugin.ReadOnlyPaths);
            if(result.ExitCode!=0) throw Failure(plugin,"La poignée de main du plugin a échoué",result);
            try { manifest=JsonSerializer.Deserialize<PluginManifest>(result.Output,Json)??throw new IOException("Manifeste plugin absent."); }
            catch(JsonException exception) { throw new IOException($"Le plugin {plugin.Id} a répondu un manifeste illisible ({exception.Message}).",exception); }
        }
        if(manifest.ContractVersion!=1 || manifest.Id!=plugin.Id || manifest.Version!=plugin.Version || !manifest.SupportedHostContractVersions.Contains(1))
            throw new IOException($"Handshake plugin incompatible : le processus annonce {manifest.Id} {manifest.Version} (contrat {manifest.ContractVersion}), le worker attend {plugin.Id} {plugin.Version} (contrat 1).");
        described[key]=(fingerprint,manifest);
        return manifest;
    }

    /// <summary>Échec d'un processus plugin : code de sortie et fin de stderr (bornée, sans retour à la ligne ni caractère de contrôle).</summary>
    private static IOException Failure(PluginLaunch plugin,string message,ToolResult result)
    {
        if(result.ExitCode==79) return new IOException($"{message} : le bac à sable Landlock n'a pas pu être appliqué (code 79).");
        var tail=new string((result.Error??"").Where(character=>!char.IsControl(character) || character==' ').ToArray()).Trim();
        if(tail.Length>MaxErrorLength) tail="…"+tail[^MaxErrorLength..];
        return new IOException($"{message} (plugin {plugin.Id}, code {result.ExitCode}){(tail.Length>0?" : "+tail:"")}.");
    }

    private static string Fingerprint(PluginLaunch plugin)
    {
        var builder=new System.Text.StringBuilder(plugin.Executable).Append('|').Append(string.Join(' ',plugin.Arguments)).Append('\n');
        var count=0;
        foreach(var root in plugin.ReadOnlyPaths.Concat(plugin.IsNative ? [] : [plugin.Executable]))
        {
            IEnumerable<string> files;
            if(File.Exists(root)) files=[root];
            else if(Directory.Exists(root)) files=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal);
            else { builder.Append("missing:").Append(root).Append('\n'); continue; }
            foreach(var file in files)
            {
                if(++count>MaxFingerprintedFiles) break;
                try { var info=new FileInfo(file); builder.Append(file).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append('\n'); }
                catch(IOException) { builder.Append(file).Append(":unreadable\n"); }
            }
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private void ValidatePaths(PluginLaunch plugin)
    {
        var protectedRoot=Path.GetFullPath(options.DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach(var candidate in plugin.ReadOnlyPaths.Concat(plugin.IsNative ? [] : [plugin.Executable]))
        {
            var path=Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            if(protectedRoot==path || protectedRoot.StartsWith(path+Path.DirectorySeparatorChar,StringComparison.Ordinal)
                || path.StartsWith(protectedRoot+Path.DirectorySeparatorChar,StringComparison.Ordinal))
                throw new IOException("Les fichiers du plugin doivent être installés en dehors des données privées du nœud.");
        }
    }
    public async Task<FlowStepResult> ExecuteAsync(FlowStepInvocation invocation,string workspace,CancellationToken ct)
    {
        var plugin=Installed.SingleOrDefault(p=>p.Id==invocation.Component.PluginId && p.Version==invocation.Component.PluginVersion)
            ??throw new IOException("Cette version du plugin n’est pas installée sur le worker.");
        if (plugin.IsNative) throw new IOException($"Le composant {plugin.Id} doit être exécuté par le moteur et ne peut pas être invoqué comme plugin de processus.");
        var manifest=await DescribeAsync(plugin,ct);
        var component=manifest.Components.FirstOrDefault(component=>component.Id==invocation.Component.ComponentId
            && component.Version==invocation.Component.ComponentVersion
            && component.ConfigurationSchema.Version==invocation.Component.ConfigurationSchemaVersion)
            ??throw new IOException("Version de composant absente du processus plugin.");
        var mediaEffect=new MediaEffectGuard(component.MediaEffect,workspace,invocation.Context.CurrentArtifactId);
        var input=JsonSerializer.Serialize(new { type="execute",contractVersion=1,invocation },Json)+"\n";
        // Le composant peut raccourcir l'échéance du bail (timeoutSeconds du manifeste), jamais la prolonger.
        var timeout=invocation.Deadline-DateTimeOffset.UtcNow;
        if(component.TimeoutSeconds is { } declared && TimeSpan.FromSeconds(declared)<timeout) timeout=TimeSpan.FromSeconds(declared);
        ToolResult result;
        try { result=await processes.RunAsync(plugin.Executable,plugin.Arguments,workspace,timeout,ct,input,1048576,true,plugin.ReadOnlyPaths); }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested)
        {
            throw new IOException($"Le plugin {plugin.Id} a dépassé son délai ({(int)timeout.TotalSeconds} s) ; son effet doit être considéré comme incertain.");
        }
        if(result.ExitCode!=0) throw Failure(plugin,"Le processus du plugin a échoué ; son effet doit être considéré comme incertain",result);
        FlowStepResult response;
        try { response=JsonSerializer.Deserialize<FlowStepResult>(result.Output,Json)??throw new IOException("Réponse plugin absente."); }
        catch(JsonException exception) { throw new IOException($"Le plugin {plugin.Id} a répondu un résultat illisible ({exception.Message}) ; son effet doit être considéré comme incertain.",
            exception); }
        if(response.ContractVersion!=1 || response.InvocationId!=invocation.InvocationId || response.LeaseEpoch!=invocation.LeaseEpoch
            || (response.OutputId is null)==(response.TerminalResult is null)) throw new IOException("Réponse plugin incompatible avec l’invocation.");
        if(response.OutputId is { } outputId && component.Outputs.All(output=>output.Id!=outputId))
            throw new IOException($"Le plugin a choisi la sortie « {outputId} » que le composant {component.Id} ne déclare pas.");
        response=response with { CandidateArtifactId=mediaEffect.Validate(response.CandidateArtifactId) };
        var changes=response.VariableChanges??new Dictionary<string,JsonElement>();
        if(changes.Count>MaxVariableChanges || changes.Keys.Any(name=>string.IsNullOrWhiteSpace(name) || name.Length>MaxVariableNameLength)
            || changes.Values.Sum(value=>value.GetRawText().Length)>MaxVariableBytes)
            throw new IOException("Le plugin renvoie trop de variables, ou des variables trop volumineuses.");
        var detail=response.Detail??"";
        return response with { VariableChanges=changes,Detail=detail.Length>MaxDetailLength?detail[..MaxDetailLength]+"…":detail };
    }
}
