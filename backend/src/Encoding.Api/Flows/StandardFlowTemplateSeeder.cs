using System.Data.Common;
using System.Text.Json;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Flows;

namespace Encoding.Api.Flows;

/// <summary>Installe les modèles standards comme copies de versions publiées, sans les exposer comme flows utilisateur.</summary>
public static class StandardFlowTemplateSeeder
{
    private static readonly DateTimeOffset SeedTimestamp = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
    private static readonly Guid[] TemplateIds =
    [
        Guid.Parse("94a10000-0000-4000-8000-000000000001"), Guid.Parse("94a10000-0000-4000-8000-000000000002"),
        Guid.Parse("94a10000-0000-4000-8000-000000000003"), Guid.Parse("94a10000-0000-4000-8000-000000000004"),
        Guid.Parse("94a10000-0000-4000-8000-000000000005"), Guid.Parse("94a10000-0000-4000-8000-000000000006"),
        Guid.Parse("94a10000-0000-4000-8000-000000000007"), Guid.Parse("94a10000-0000-4000-8000-000000000008"),
        Guid.Parse("94a10000-0000-4000-8000-000000000009"), Guid.Parse("94a10000-0000-4000-8000-000000000010"),
        Guid.Parse("94a10000-0000-4000-8000-000000000011"), Guid.Parse("94a10000-0000-4000-8000-000000000012"),
        Guid.Parse("94a10000-0000-4000-8000-000000000013"), Guid.Parse("94a10000-0000-4000-8000-000000000014"),
        Guid.Parse("94a10000-0000-4000-8000-000000000015"), Guid.Parse("94a10000-0000-4000-8000-000000000016"),
        Guid.Parse("94a10000-0000-4000-8000-000000000017"),
    ];

    private sealed record Template(string Name, string Description, FlowDefinition Definition);
    private sealed record Node(string Id, string Plugin, string Component, object Configuration);
    private sealed record Edge(string From, string Output, string To);

    public static async Task EnsureAsync(DbDataSource dataSource, FlowComponentRegistry registry,
        FlowDefinitionValidator validator, CancellationToken ct, ILogger? logger = null)
    {
        IReadOnlyList<Template> templates;
        try { templates = Build(registry); }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Le composant intégré", StringComparison.Ordinal))
        {
            logger?.LogInformation("Les modèles standards attendent les paquets de composants nécessaires du Store : {Reason}", exception.Message);
            return;
        }
        if (templates.Count != TemplateIds.Length)
            throw new InvalidOperationException("Le catalogue des modèles standards n’a pas le nombre attendu d’entrées.");

        for (var i = 0; i < templates.Count; i++)
        {
            var templateId = TemplateIds[i];
            var template = templates[i] with { Definition = templates[i].Definition with { Id = SourceFlowId(templateId) } };
            var validation = validator.Validate(template.Definition);
            if (!validation.IsValid)
                throw new InvalidOperationException($"Le modèle standard « {template.Name} » est invalide : " +
                    string.Join("; ", validation.Issues.Select(issue => issue.Code + " " + issue.Path + " " + issue.Message)));
            await EnsureOneAsync(dataSource, TemplateIds[i], template, ct);
        }
    }

    private static async Task EnsureOneAsync(DbDataSource dataSource, Guid templateId, Template template, CancellationToken ct)
    {
        var sourceFlowId = SourceFlowId(templateId);
        var json = JsonSerializer.Serialize(template.Definition, FlowRepository.JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var existing = Sql.Command("SELECT id,source_flow_id,source_version,definition FROM flow_templates WHERE id=@id OR lower(name)=lower(@name)", connection, transaction))
        {
            existing.Parameters.AddWithValue("id", templateId);
            existing.Parameters.AddWithValue("name", template.Name);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var existingTemplateId = reader.GetString(0);
                var existingFlowId = reader.GetString(1);
                var existingVersion = reader.GetInt32(2);
                var existingDefinition = reader.GetString(3);
                await reader.DisposeAsync();
                if (!string.Equals(existingTemplateId, templateId.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.CommitAsync(ct);
                    return;
                }
                var currentDefinition = template.Definition with
                {
                    Id = Guid.Parse(existingFlowId),
                    Version = existingVersion,
                };
                var currentJson = JsonSerializer.Serialize(currentDefinition, FlowRepository.JsonOptions);
                if (!string.Equals(existingDefinition, currentJson, StringComparison.Ordinal))
                {
                    var nextVersion = existingVersion + 1;
                    var nextDefinition = currentDefinition with { Version = nextVersion };
                    var nextJson = JsonSerializer.Serialize(nextDefinition, FlowRepository.JsonOptions);
                    await using (var version = Sql.Command("INSERT INTO flow_versions(flow_id,version,definition,created_at) VALUES(@id,@version,@definition,@timestamp) ON CONFLICT(flow_id,version) DO NOTHING",
                        connection, transaction))
                    {
                        version.Parameters.AddWithValue("id", existingFlowId);
                        version.Parameters.AddWithValue("version", nextVersion);
                        version.Parameters.AddWithValue("definition", SqlKind.Json, nextJson);
                        version.Parameters.AddWithValue("timestamp", timestamp);
                        await version.ExecuteNonQueryAsync(ct);
                    }
                    await using (var flow = Sql.Command("UPDATE flows SET current_version=@version,updated_at=@timestamp WHERE id=@id", connection, transaction))
                    {
                        flow.Parameters.AddWithValue("id", existingFlowId);
                        flow.Parameters.AddWithValue("version", nextVersion);
                        flow.Parameters.AddWithValue("timestamp", timestamp);
                        await flow.ExecuteNonQueryAsync(ct);
                    }
                    await using (var templateUpdate = Sql.Command("UPDATE flow_templates SET source_version=@version,definition=@definition,description=@description WHERE id=@id",
                        connection, transaction))
                    {
                        templateUpdate.Parameters.AddWithValue("id", templateId);
                        templateUpdate.Parameters.AddWithValue("version", nextVersion);
                        templateUpdate.Parameters.AddWithValue("definition", SqlKind.Json, nextJson);
                        templateUpdate.Parameters.AddWithValue("description", template.Description);
                        await templateUpdate.ExecuteNonQueryAsync(ct);
                    }
                    await using (var backup = Sql.Command("INSERT INTO flow_backup_jobs(flow_id,version,status,next_attempt_at) VALUES(@id,@version,'pending',@timestamp) ON CONFLICT(flow_id,version) DO NOTHING",
                        connection, transaction))
                    {
                        backup.Parameters.AddWithValue("id", existingFlowId);
                        backup.Parameters.AddWithValue("version", nextVersion);
                        backup.Parameters.AddWithValue("timestamp", timestamp);
                        await backup.ExecuteNonQueryAsync(ct);
                    }
                }
                else
                {
                    await using var templateUpdate = Sql.Command("UPDATE flow_templates SET description=@description WHERE id=@id", connection, transaction);
                    templateUpdate.Parameters.AddWithValue("id", templateId);
                    templateUpdate.Parameters.AddWithValue("description", template.Description);
                    await templateUpdate.ExecuteNonQueryAsync(ct);
                }
                await transaction.CommitAsync(ct);
                return;
            }
            await reader.DisposeAsync();
        }

        timestamp = SeedTimestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await using (var flow = Sql.Command("INSERT INTO flows(id,current_version,updated_at,deleted_at) VALUES(@id,1,@timestamp,@timestamp) ON CONFLICT(id) DO NOTHING", connection, transaction))
        {
            flow.Parameters.AddWithValue("id", sourceFlowId);
            flow.Parameters.AddWithValue("timestamp", timestamp);
            await flow.ExecuteNonQueryAsync(ct);
        }
        await using (var version = Sql.Command("INSERT INTO flow_versions(flow_id,version,definition,created_at) VALUES(@id,1,@definition,@timestamp) ON CONFLICT(flow_id,version) DO NOTHING",
            connection, transaction))
        {
            version.Parameters.AddWithValue("id", sourceFlowId);
            version.Parameters.AddWithValue("definition", SqlKind.Json, json);
            version.Parameters.AddWithValue("timestamp", timestamp);
            await version.ExecuteNonQueryAsync(ct);
        }
        await using (var backup = Sql.Command("INSERT INTO flow_backup_jobs(flow_id,version,status,next_attempt_at) VALUES(@id,1,'pending',@timestamp) ON CONFLICT(flow_id,version) DO NOTHING",
            connection, transaction))
        {
            backup.Parameters.AddWithValue("id", sourceFlowId);
            backup.Parameters.AddWithValue("timestamp", timestamp);
            await backup.ExecuteNonQueryAsync(ct);
        }
        await using (var saved = Sql.Command("""
            INSERT INTO flow_templates(id,name,description,source_flow_id,source_version,source_flow_name,media_type,definition,created_at)
            VALUES(@id,@name,@description,@sourceFlowId,1,@name,@mediaType,@definition,@timestamp)
            """, connection, transaction))
        {
            saved.Parameters.AddWithValue("id", templateId);
            saved.Parameters.AddWithValue("name", template.Name);
            saved.Parameters.AddWithValue("description", template.Description);
            saved.Parameters.AddWithValue("sourceFlowId", sourceFlowId);
            saved.Parameters.AddWithValue("mediaType", template.Definition.MediaType);
            saved.Parameters.AddWithValue("definition", SqlKind.Json, json);
            saved.Parameters.AddWithValue("timestamp", timestamp);
            await saved.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static IReadOnlyList<Template> Build(FlowComponentRegistry registry)
    {
        var result = new List<Template>();
        FlowDefinition Make(string name, string mediaType, params Node[] nodes) =>
            Chain(registry, Guid.Empty, name, mediaType, nodes);
        void Add(string name, string description, FlowDefinition definition) =>
            result.Add(new(name, description, definition));

        Add("Vérifier une vidéo", "Analyse la vidéo et vérifie le décodage de ses pistes sans réencoder ni remplacer le fichier.",
            Make("Vérifier une vidéo", "video",
                new("input", "base", "base-input-video", new { }),
                new("verify", "video", "video-verify-decoding", new { trackType = "all" }),
                new("done", "base", "base-complete-flow", new { result = "success", message = "Vérification du décodage terminée." })));

        Add("Remuxer une vidéo en MKV", "Copie les pistes compatibles vers un conteneur MKV, sans réencoder. La source reste préservée.",
            ProcessingFlow(registry, "Remuxer une vidéo en MKV", "video", "video-remux-media",
                new { processingMode = "plan", container = "mkv" }));

        Add("Convertir une vidéo en H.265 / HEVC", "Flow vidéo générique vers MKV, qualité équilibrée, pistes audio et sous-titres conservées, repli CPU désactivé.",
            GuidedFlowBuilder.Build(new(TemplateIds[2], "Convertir une vidéo en H.265 / HEVC", "hevc", "automatic", "balanced", "keep", false), registry));
        Add("Convertir une vidéo en H.264 compatible", "Flow vidéo générique vers MKV et H.264, avec conservation des pistes audio et sous-titres.",
            GuidedFlowBuilder.Build(new(TemplateIds[3], "Convertir une vidéo en H.264 compatible", "h264", "automatic", "balanced", "keep", false), registry));

        Add("Analyser un fichier audio", "Lit les caractéristiques audio et conserve le rapport dans la variable mediaDetails.",
            Make("Analyser un fichier audio", "music",
                new("input", "base", "base-input-music", new { }),
                new("inspect", "probe", "probe-audio", new { trackId = 0, resultVariable = "mediaDetails", refreshMetadata = false }),
                new("done", "base", "base-complete-flow", new { result = "success", message = "Analyse audio terminée." })));

        Add("Convertir un fichier audio en AAC", "Convertit la piste principale en AAC stéréo à 192 kbit/s, avec contrôle structurel et publication du résultat.",
            ProcessingFlow(registry, "Convertir un fichier audio en AAC", "music", "audio-transcode",
                new { processingMode = "plan", trackId = 0, audioCodec = "aac", bitrateKbps = 192, channels = "2" }));

        Add("Normaliser le volume audio", "Normalise la piste principale avec le profil de diffusion en continu, sans toucher à la source.",
            ProcessingFlow(registry, "Normaliser le volume audio", "music", "audio-normalize",
                new { processingMode = "plan", trackId = 0, targetLoudness = -16, truePeak = -1.5, normalizationProfile = "streaming", normalizationMethod = "single", loudnessRange = 7 }));

        Add("Convertir une image en WebP", "Convertit l’image en WebP avec une qualité de départ de 85, puis valide et publie la sortie.",
            ImageFlow(registry, "Convertir une image en WebP", "image-convert", new { format = "webp", quality = 85 }));
        Add("Redimensionner une image", "Limite l’image à 1920 × 1080 en conservant ses proportions.",
            ImageFlow(registry, "Redimensionner une image", "image-resize", new { width = 1920, height = 1080, preserveAspectRatio = true }));
        Add("Recadrer une image au format 16:9", "Produit un recadrage 16:9 centré. Ajustez les dimensions et le cadrage dans l’éditeur.",
            ImageFlow(registry, "Recadrer une image au format 16:9", "image-crop", new { aspectRatio = "16:9", x = 0, y = 0, width = 1920, height = 1080 }));

        Add("Analyser un fichier de sous-titres", "Lit les métadonnées disponibles sur le fichier de sous-titres sans le modifier.",
            Make("Analyser un fichier de sous-titres", "subtitles",
                new("input", "base", "base-input-subtitles", new { }),
                new("inspect", "probe", "probe-media", new { resultVariable = "mediaDetails", refreshMetadata = false }),
                new("done", "base", "base-complete-flow", new { result = "success", message = "Analyse des sous-titres terminée." })));
        Add("Extraire les sous-titres d’une vidéo", "Extrait les pistes de sous-titres d’un MKV dans leur format natif, sans modifier la vidéo source.",
            Make("Extraire les sous-titres d’une vidéo", "video",
                new("input", "base", "base-input-video", new { }),
                new("extract", "subtitles", "subtitles-extract", new { trackIds = "" }),
                new("done", "base", "base-complete-flow", new { result = "success", message = "Extraction des sous-titres terminée." })));
        Add("Uniformiser les sous-titres d’une vidéo", "Applique les règles intégrées aux sous-titres de la copie de travail et conserve les autres pistes.",
            Make("Uniformiser les sous-titres d’une vidéo", "video",
                new("input", "base", "base-input-video", new { }),
                new("manage", "subtitles", "subtitles-manage", new { styleProfile = "crunchyroll", convertSrt = true, upgradeAss = true,
                    removeDuplicates = true, inferForced = true, setTitles = true, frenchDefault = true, fontFamily = "Trebuchet MS", fontSize = 23,
                    fontOutline = 2.5, fontMargin = 20 }),
                new("done", "base", "base-complete-flow", new { result = "success", message = "Uniformisation des sous-titres terminée." })));

        Add("Actualiser le film dans Radarr", "Actualise les informations du film après le traitement. Requiert une configuration Radarr.",
            IntegrationFlow(registry, "Actualiser le film dans Radarr", "radarr", "radarr-refresh-movie"));
        Add("Actualiser la série dans Sonarr", "Actualise les informations de la série après le traitement. Requiert une configuration Sonarr.",
            IntegrationFlow(registry, "Actualiser la série dans Sonarr", "sonarr", "sonarr-refresh-series"));
        Add("Actualiser la bibliothèque Plex", "Demande à Plex d’actualiser la bibliothèque après le traitement. Requiert une configuration Plex.",
            IntegrationFlow(registry, "Actualiser la bibliothèque Plex", "plex", "plex-refresh-library"));
        Add("Actualiser l’élément Jellyfin", "Demande à Jellyfin d’actualiser les métadonnées après le traitement. Requiert une configuration Jellyfin.",
            IntegrationFlow(registry, "Actualiser l’élément Jellyfin", "jellyfin", "jellyfin-refresh-item"));

        return result;
    }

    private static FlowDefinition Chain(FlowComponentRegistry registry, Guid id, string name, string mediaType, params Node[] nodes)
    {
        var definitions = nodes.Select(node => CreateNode(registry, node)).ToArray();
        return new(1, id, 1, name, mediaType, definitions,
            Enumerable.Range(0, nodes.Length - 1).Select(i => new FlowConnectionDefinition($"link-{i + 1}", nodes[i].Id, "success", nodes[i + 1].Id)).ToArray());
    }

    private static FlowDefinition ProcessingFlow(FlowComponentRegistry registry, string name, string mediaType, string componentId, object configuration)
    {
        var nodes = new[]
        {
            CreateNode(registry, new("input", "base", "base-input-" + (mediaType == "music" ? "music" : "video"), new { })),
            CreateNode(registry, new("start", "base", FfmpegBuilderPolicy.Start, new { preserveUnchangedTracks = true })),
            CreateNode(registry, new("action", componentId.StartsWith("audio-", StringComparison.Ordinal) ? "audio" : "video", componentId, configuration)),
            CreateNode(registry, new("execute", "base", FfmpegBuilderPolicy.Execute, new { })),
            CreateNode(registry, new("publish", "base", "base-publish-result", new { })),
            CreateNode(registry, new("published", "base", "base-complete-flow", new { result = "success", message = "Traitement terminé et publié." })),
            CreateNode(registry, new("unchanged", "base", "base-complete-flow", new { result = "unchanged", message = "Aucune modification nécessaire." })),
        };
        var edges = new List<FlowConnectionDefinition>
        {
            new FlowConnectionDefinition("link-1", "input", "success", "start"),
            new FlowConnectionDefinition("link-2", "start", "success", "action"),
            new FlowConnectionDefinition("link-3", "action", "success", "execute"),
            new FlowConnectionDefinition("link-4", "execute", "success", "publish"),
            new FlowConnectionDefinition("link-5", "execute", "skipped", "unchanged"),
            new FlowConnectionDefinition("link-6", "publish", "published", "published"),
            new FlowConnectionDefinition("link-7", "publish", "unchanged", "unchanged"),
        };
        if (componentId.StartsWith("audio-", StringComparison.Ordinal))
            edges.Add(new("link-audio-unchanged", "action", "unchanged", "execute"));
        return new(1, Guid.Empty, 1, name, mediaType, nodes, edges);
    }

    private static FlowDefinition ImageFlow(FlowComponentRegistry registry, string name, string componentId, object configuration)
    {
        var nodes = new[]
        {
            CreateNode(registry, new("input", "base", "base-input-image", new { })),
            CreateNode(registry, new("action", "images", componentId, configuration)),
            CreateNode(registry, new("publish", "base", "base-publish-result", new { })),
            CreateNode(registry, new("published", "base", "base-complete-flow", new { result = "success", message = "Image traitée et publiée." })),
            CreateNode(registry, new("unchanged", "base", "base-complete-flow", new { result = "unchanged", message = "Aucune modification nécessaire." })),
        };
        return new(1, Guid.Empty, 1, name, "image", nodes,
        [
            new("link-1", "input", "success", "action"),
            new("link-2", "action", "success", "publish"),
            new("link-3", "publish", "published", "published"),
            new("link-4", "publish", "unchanged", "unchanged"),
        ]);
    }

    private static FlowDefinition IntegrationFlow(FlowComponentRegistry registry, string name, string plugin, string componentId) =>
        Chain(registry, Guid.Empty, name, "video",
            new("input", "base", "base-input-video", new { }),
            new("integration", plugin, componentId, new { }),
            new("done", "base", "base-complete-flow", new { result = "success" }));

    private static FlowNodeDefinition CreateNode(FlowComponentRegistry registry, Node node)
    {
        var reference = registry.FindReference(node.Component, node.Plugin)
            ?? throw new InvalidOperationException($"Le composant intégré « {node.Plugin}/{node.Component} » est introuvable.");
        var configuration = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(node.Configuration, FlowRepository.JsonOptions), FlowRepository.JsonOptions)
            ?? throw new InvalidOperationException($"La configuration de « {node.Component} » est invalide.");
        return new(node.Id, null, reference, configuration);
    }

    private static Guid SourceFlowId(Guid templateId) =>
        Guid.Parse(templateId.ToString()[..^1] + "8");
}
