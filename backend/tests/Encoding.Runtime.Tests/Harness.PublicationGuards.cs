using System.Text.Json;
using Encoding.Api.Plugins;

namespace Encoding.Runtime.Tests;

internal static partial class Harness
{
    internal sealed partial class Fixture
    {
        public async Task PublicationGuardsAsync()
        {
            var plex = registry.Plugins.Single(item => item.Id == "plex");
            var provider = new PublicationGuardProvider(configurations, registry);
            Check((await provider.ListAsync(CancellationToken.None)).Count == 0, "A plugin that was never configured provides no guard.");

            var saved = await configurations.SaveGlobalAsync(plex, new(0, new Dictionary<string, JsonElement> { ["baseUrl"] = J("http://plex:32400"), ["apiKey"] = J("jeton-plex-secret") }), CancellationToken.None);
            var guard = (await provider.ListAsync(CancellationToken.None)).Single();
            Check(guard.PluginId == "plex" && guard.PluginVersion == "1.1.0" && guard.ComponentId == "plex-check-playback" && guard.ComponentVersion == "1.0.0",
                "A configured Plex guards replacements by default.");
            Check(guard.Configuration["scope"].GetString() == "file" && guard.Configuration["includeNextEpisode"].GetBoolean() && guard.Configuration["nextEpisodeMarginMinutes"].GetInt32() == 15 && guard.MaxWaitMinutes == 10,
                "The schema defaults apply: file scope, next episode, 15 min margin, 10 min maximum wait.");
            Check(guard.PluginConfiguration["apiKey"].GetString() == "jeton-plex-secret" && guard.WaitOutputs.SequenceEqual(["playing"]) && guard.AllowOutputs.SequenceEqual(["idle"]),
                "The access node receives the effective plugin values and the output mapping.");

            await configurations.SaveGlobalAsync(plex, new(saved!.Revision, new Dictionary<string, JsonElement>
                { ["guardIncludeNextEpisode"] = J(false), ["guardNextEpisodeMarginMinutes"] = J(5), ["guardMaxWaitMinutes"] = J(20) }), CancellationToken.None);
            var tuned = (await provider.ListAsync(CancellationToken.None)).Single();
            Check(!tuned.Configuration["includeNextEpisode"].GetBoolean() && tuned.Configuration["nextEpisodeMarginMinutes"].GetInt32() == 5 && tuned.MaxWaitMinutes == 20, "Saved guard settings are used.");

            var tunedRevision = (await configurations.GetGlobalAsync(plex, CancellationToken.None)).Revision;
            await configurations.SaveGlobalAsync(plex, new(tunedRevision, new Dictionary<string, JsonElement> { ["guardReplacement"] = J(false) }), CancellationToken.None);
            Check((await provider.ListAsync(CancellationToken.None)).Count == 0, "Turning the guard off provides none.");
        }
    }
}
