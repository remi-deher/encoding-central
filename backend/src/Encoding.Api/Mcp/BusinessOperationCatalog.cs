using System.Text.Json.Nodes;

namespace Encoding.Api.Mcp;

/// <summary>Metadata for a business operation exposed through the REST, MCP and assistant surfaces.</summary>
public sealed record BusinessOperationDescriptor(
    string Name,
    string Domain,
    string Description,
    bool ReadOnly,
    bool RequiresAdministrator,
    bool AvailableToAssistant,
    object InputSchema,
    bool RequiresConfirmation = false);

/// <summary>
/// Shared operation inventory. Input schemas continue to use MCP's JSON-schema contract so
/// API clients, MCP clients and the assistant receive the same definition.
/// </summary>
public static partial class BusinessOperationCatalog
{
    public static readonly IReadOnlyList<McpToolDefinition> ToolDefinitions =
    [
        new("get_version", "Consulte la version installée et le commit du nœud serveur interrogé.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }),
        new("get_updates", "Consulte les notes de version et la disponibilité d’une mise à jour pour ce nœud.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }),
        // --- 1. BIBLIOTHÈQUES & SANTÉ DU CLUSTER ---
        new(
            "get_libraries_overview",
            "Obtient une vue d'ensemble complète et consolidée de toutes les bibliothèques multimédias du cluster (flows associés, modes d'accès par nœud, état des scans et compteurs de fichiers).",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new("list_libraries", "Liste les bibliothèques, leur activation et les flux qui leur sont affectés.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }),
        new("create_library", "Crée une bibliothèque vide. Nécessite les droits administrateur.",
            new { type = "object", properties = new { name = new { type = "string" } }, required = new[] { "name" } }),
        new("update_library", "Renomme une bibliothèque sans modifier ses médias. Nécessite les droits administrateur.",
            new { type = "object", properties = new { libraryId = new { type = "string" }, name = new { type = "string" } }, required = new[] { "libraryId", "name" } }),
        new("delete_library", "Supprime la définition d’une bibliothèque, sans supprimer les fichiers média. Nécessite les droits administrateur.",
            new { type = "object", properties = new { libraryId = new { type = "string" } }, required = new[] { "libraryId" } }),
        new("get_library_node_access", "Consulte les droits et chemins locaux déclarés pour chaque nœud d’une bibliothèque.",
            new { type = "object", properties = new { libraryId = new { type = "string" } }, required = new[] { "libraryId" } }),
        new("get_library_workflow", "Consulte le flow affecté, l’état de découverte et les fichiers détectés d’une bibliothèque.",
            new { type = "object", properties = new { libraryId = new { type = "string" }, search = new { type = "string" }, limit = new { type = "integer" },
                offset = new { type = "integer" } }, required = new[] { "libraryId" } }),
        new("get_library_processing", "Consulte la surveillance automatique et les limites de traitement d’une bibliothèque.",
            new { type = "object", properties = new { libraryId = new { type = "string" } }, required = new[] { "libraryId" } }),
        new("save_library_processing", "Configure la surveillance automatique et les limites de traitement d’une bibliothèque. Nécessite les droits administrateur.",
            new { type = "object", additionalProperties = false, properties = new { libraryId = new { type = "string", format = "uuid" },
                watchEnabled = new { type = "boolean" }, scanIntervalSeconds = new { type = "integer", minimum = 15, maximum = 86400 },
                maxConcurrentTasks = new { type = new[] { "integer", "null" }, minimum = 1, maximum = 1000 }, nodeLimits = new { type = "array",
                maxItems = 1000, items = new { type = "object", additionalProperties = false, properties = new { nodeId = new { type = "string",
                format = "uuid" }, maxConcurrentTasks = new { type = "integer", minimum = 1, maximum = 1000 } }, required = new[] { "nodeId",
                "maxConcurrentTasks" } } }, expectedRevision = new { type = "integer", minimum = 0 } }, required = new[] { "libraryId", "watchEnabled",
                "scanIntervalSeconds", "nodeLimits", "expectedRevision" } }),
        new("configure_library_node_access",
            "Configure les modes d’accès direct, indisponible ou par transfert, avec les droits de lecture et d’écriture par nœud. Nécessite les droits administrateur.",
            new { type = "object", additionalProperties = false, properties = new { libraryId = new { type = "string", format = "uuid" },
                nodes = new { type = "array", maxItems = 2048,
                description = "Accès par nœud : nodeId, mode (direct|unsupported|transfer), localPath, canRead, canWrite, transferSourceNodeId, isEnabled.",
                items = new { type = "object", additionalProperties = false, properties = new { nodeId = new { type = "string", format = "uuid" },
                mode = new { type = "string", @enum = new[] { "direct", "unsupported", "transfer" } }, localPath = new { type = new[] { "string", "null" },
                maxLength = 4096 }, canRead = new { type = "boolean" }, canWrite = new { type = "boolean" },
                transferSourceNodeId = new { type = new[] { "string", "null" }, format = "uuid" }, isEnabled = new { type = "boolean" } },
                required = new[] { "nodeId", "mode", "canRead", "canWrite" } } } }, required = new[] { "libraryId", "nodes" } }),
        new("set_library_activation", "Active ou désactive une bibliothèque. Nécessite les droits administrateur.",
            new { type = "object", properties = new { libraryId = new { type = "string" }, isActive = new { type = "boolean" } }, required = new[] { "libraryId", "isActive" } }),
        new("assign_library_flow", "Affecte ou retire une version publiée d’un flow à une bibliothèque. Nécessite les droits administrateur.",
            new { type = "object", properties = new { libraryId = new { type = "string" }, flowId = new { type = "string" }, version = new { type = "integer" },
                expectedRevision = new { type = "integer" } }, required = new[] { "libraryId", "expectedRevision" } }),
        new("request_library_discovery", "Demande une découverte des fichiers d’une bibliothèque active. Nécessite les droits administrateur.",
            new { type = "object", properties = new { libraryId = new { type = "string" } }, required = new[] { "libraryId" } }),
        new(
            "list_nodes",
            "Liste tous les nœuds du cluster, leurs rôles (serveur, worker), état de présence (en ligne/hors ligne) et capacités d'encodage.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new("list_node_groups", "Liste les groupes de nœuds et leurs critères, étiquettes et membres.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }),
        new("get_node_group", "Consulte les critères et membres d’un groupe de nœuds.",
            new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } }),
        new("save_node_group", "Crée ou modifie un groupe manuel ou automatique. Nécessite les droits administrateur.",
            new { type = "object", additionalProperties = false, properties = new { id = new { type = "string", format = "uuid" }, name = new { type = "string",
                minLength = 1, maxLength = 128 }, description = new { type = "string", maxLength = 2000 }, mode = new { type = "string",
                @enum = new[] { "manual", "automatic" } }, criteria = new { type = "object", additionalProperties = false,
                properties = new { roles = new { type = "array", maxItems = 2, items = new { type = "string", @enum = new[] { "server", "worker" } } },
                operatingSystem = new { type = new[] { "string", "null" }, maxLength = 128 }, architecture = new { type = new[] { "string", "null" },
                maxLength = 128 }, minimumMemoryMb = new { type = new[] { "integer", "null" }, minimum = 1 }, requiredToolIds = new { type = "array",
                maxItems = 128, items = new { type = "string", maxLength = 128 } }, minimumConcurrentTasks = new { type = new[] { "integer", "null" },
                minimum = 1 }, minimumProcessorCount = new { type = new[] { "integer", "null" }, minimum = 1 }, requiredEncoders = new { type = "array",
                maxItems = 128, items = new { type = "string", maxLength = 128 } } } }, labels = new { type = "object", maxProperties = 128,
                additionalProperties = new { type = new[] { "string", "null" }, maxLength = 1024 } }, nodeIds = new { type = "array", maxItems = 2048,
                items = new { type = "string", format = "uuid" } } }, required = new[] { "name", "description", "mode", "criteria", "labels", "nodeIds" } }),
        new("delete_node_group", "Supprime un groupe non utilisé par un flow. Nécessite les droits administrateur.",
            new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } }),
        new("get_node_activity", "Consulte la capacité, la charge et l’activité récente d’un nœud.",
            new { type = "object", properties = new { nodeId = new { type = "string" } }, required = new[] { "nodeId" } }),
        new("get_node_identity", "Consulte le nom, les étiquettes et les groupes effectifs d’un nœud.",
            new { type = "object", properties = new { nodeId = new { type = "string" } }, required = new[] { "nodeId" } }),
        new("set_node_draining", "Demande ou annule la vidange d’un nœud pour empêcher les nouvelles affectations pendant une maintenance. Nécessite les droits administrateur.",
            new { type = "object", properties = new { nodeId = new { type = "string" }, draining = new { type = "boolean" } }, required = new[] { "nodeId", "draining" } }),
        new("get_node_processing", "Consulte la capacité de traitement et la plage horaire d’un nœud.",
            new { type = "object", properties = new { nodeId = new { type = "string" } }, required = new[] { "nodeId" } }),
        new("save_node_processing", "Configure la capacité concurrente et la plage horaire d’un nœud. Nécessite les droits administrateur.",
            new { type = "object", additionalProperties = false, properties = new { nodeId = new { type = "string", format = "uuid" },
                maxConcurrentTasks = new { type = new[] { "integer", "null" }, minimum = 1, maximum = 1000 }, schedule = new { type = new[] { "object",
                "null" }, additionalProperties = false, properties = new { enabled = new { type = "boolean" }, timeZone = new { type = "string", minLength = 1,
                maxLength = 64 }, slots = new { type = "string", minLength = 672, maxLength = 672, pattern = "^[01]{672}$" } }, required = new[] { "enabled",
                "timeZone", "slots" } }, expectedRevision = new { type = "integer", minimum = 0 } }, required = new[] { "nodeId", "expectedRevision" } }),
        new("save_node_identity", "Modifie le nom convivial et les étiquettes déclarées d’un nœud. Nécessite les droits administrateur.",
            new { type = "object", properties = new { nodeId = new { type = "string" }, displayName = new { type = "string" },
                labels = new { type = "object" } }, required = new[] { "nodeId", "labels" } }),
        new(
            "list_executions",
            "Liste les exécutions de traitement récentes ou filtrées par état (queued, running, failed, completed).",
            new
            {
                type = "object",
                properties = new
                {
                    state = new { type = "string", description = "Filtrer par état (queued, running, failed, completed)." },
                    limit = new { type = "integer", description = "Nombre maximal de résultats (défaut 50, max 200).", minimum = 1, maximum = 200, @default = 50 }
                },
                required = Array.Empty<string>()
            }
        ),
        new("get_execution", "Consulte l’état et les détails d’une exécution.",
            new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } }),
        new("get_execution_events", "Consulte le journal d’événements d’une exécution.",
            new { type = "object", properties = new { id = new { type = "string" }, afterRevision = new { type = "integer" }, limit = new { type = "integer" } }, required = new[] { "id" } }),
        new("get_execution_pipeline", "Consulte les étapes du flow réellement parcourues par une exécution.",
            new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } }),
        new("cancel_execution", "Demande l’annulation d’une exécution en cours ou en attente. Nécessite les droits administrateur.",
            new { type = "object", properties = new { id = new { type = "string" }, expectedRevision = new { type = "integer" } }, required = new[] { "id" } }),
        new("retry_execution", "Relance une exécution en échec depuis sa source avec une nouvelle tentative. Nécessite les droits administrateur.",
            new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } }),
        new("resume_execution", "Relance une exécution annulée en créant une nouvelle exécution depuis le fichier source. Une seule reprise est autorisée. Nécessite les droits administrateur.",
            new { type = "object", properties = new { id = new { type = "string" } }, required = new[] { "id" } }),

        new(
            "get_system_logs",
            "Consulte les journaux système et nœuds récents pour analyser les incidents, pannes matérielles, erreurs de plugins ou avertissements du cluster.",
            new
            {
                type = "object",
                properties = new
                {
                    min_level = new { type = "string",
                        description = "Sévérité minimale des logs retournés (Trace, Debug, Information, Warning, Error, Critical). Défaut: Warning.",
                        @default = "Warning" },
                    category = new { type = "string", description = "Préfixe de catégorie de log (ex. 'Encoding.NodeAgent', 'Encoding.Plugins')." },
                    search = new { type = "string", description = "Recherche textuelle dans le message ou l'exception du log." },
                    limit = new { type = "integer", description = "Nombre maximal d'entrées (défaut: 50, max: 200).", @default = 50 },
                    node_id = new { type = "string", description = "Identifiant UUID optionnel du nœud concerné." }
                },
                required = Array.Empty<string>()
            }
        ),

        // --- 2. FLOWS ---
        new(
            "get_flow_schema",
            "Introspecte le catalogue de composants de flows disponibles (identifiants, types d'entrées/sorties, paramètres configurables) et fournit un exemple de flow JSON valide.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "list_flows",
            "Liste tous les flows configurés dans le cluster avec leurs versions publiées, leur type et statut de brouillon.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_flow",
            "Récupère la définition complète d'un flow (ou de son brouillon) par son identifiant UUID.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du flow." }
                },
                required = new[] { "id" }
            }
        ),
        new(
            "create_flow",
            "Crée et valide un nouveau flow à partir de sa définition JSON. Peut être publié immédiatement ou enregistré en brouillon. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    id = new { type = "string", format = "uuid", description = "Identifiant UUID facultatif du flow." },
                    name = new { type = "string", minLength = 1, maxLength = 128, description = "Nom du flow." },
                    type = new { type = "string", @enum = new[] { "video", "image", "music", "subtitles" }, description = "Type de média du flow.", @default = "video" },
                    mediaType = new { type = "string", @enum = new[] { "video", "image", "music", "subtitles" } },
                    formatVersion = new { type = "integer", minimum = 1, maximum = 1 },
                    version = new { type = "integer", minimum = 1 },
                    publish = new { type = "boolean", description = "Si true, valide et publie le flow immédiatement en version 1.", @default = true },
                    nodes = new { type = "array", minItems = 1, maxItems = 1000,
                        description = "Liste des nœuds du flow avec id, component, name facultatif et configuration.", items = new { type = "object",
                        additionalProperties = false, properties = new { id = new { type = "string", minLength = 1, maxLength = 256 },
                        name = new { type = new[] { "string", "null" }, maxLength = 128 }, component = new { type = new[] { "string", "object" },
                        maxLength = 256, additionalProperties = false, properties = new { id = new { type = "string", maxLength = 256 },
                        componentId = new { type = "string", maxLength = 256 }, pluginId = new { type = "string", maxLength = 128 },
                        pluginVersion = new { type = "string", maxLength = 64 }, componentVersion = new { type = "string", maxLength = 64 },
                        configurationSchemaVersion = new { type = "integer", minimum = 1 } } }, configuration = new { type = "object", maxProperties = 256,
                        additionalProperties = true } }, required = new[] { "component" } } },
                    connections = new { type = "array", maxItems = 4000, description = "Liste des connexions orientées entre sorties et entrées de nœuds.",
                        items = new { type = "object", additionalProperties = false, properties = new { id = new { type = "string", minLength = 1,
                        maxLength = 256 }, sourceNodeId = new { type = "string", minLength = 1, maxLength = 256 }, sourceOutputId = new { type = "string",
                        minLength = 1, maxLength = 256 }, targetNodeId = new { type = "string", minLength = 1, maxLength = 256 } },
                        required = new[] { "sourceNodeId", "targetNodeId" } } }
                },
                required = new[] { "nodes", "connections" }
            }
        ),
        new(
            "clone_flow",
            "Duplique un flow existant sous un nouveau nom. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    sourceFlowId = new { type = "string", description = "Identifiant UUID du flow à cloner." },
                    newName = new { type = "string", description = "Nom donné au nouveau flow dupliqué." }
                },
                required = new[] { "sourceFlowId", "newName" }
            }
        ),
        new(
            "delete_flow",
            "Supprime un flow du cluster (suppression logique préservant l'audit s'il a des versions publiées, définitive pour un brouillon). Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    flowId = new { type = "string", description = "Identifiant UUID du flow à supprimer." }
                },
                required = new[] { "flowId" }
            }
        ),

        // --- 3. SCRIPTS (JAVASCRIPT / PYTHON) ---
        new(
            "list_scripts",
            "Liste tous les scripts partagés réutilisables du cluster avec leur langage (javascript, python) et versions publiées.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_script",
            "Récupère le détail d'un script et le code source complet de son brouillon.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du script." }
                },
                required = new[] { "id" }
            }
        ),
        new(
            "save_script",
            "Crée ou met à jour le brouillon d'un script (javascript ou python). Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du script existant (omettre pour créer un nouveau script)." },
                    name = new { type = "string", description = "Nom du script (max 128 caractères)." },
                    language = new { type = "string", description = "Langage du script : 'javascript' ou 'python'.", @enum = new[] { "javascript", "python" } },
                    description = new { type = "string", description = "Description du rôle du script (max 1000 caractères)." },
                    code = new { type = "string", description = "Code source du script." },
                    expectedRevision = new { type = "integer", description = "Révision attendue pour mise à jour d'un script existant (défaut 1)." }
                },
                required = new[] { "name", "language", "code" }
            }
        ),
        new(
            "publish_script",
            "Publie une version immuable du brouillon d'un script pour utilisation en production. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du script." },
                    expectedRevision = new { type = "integer", description = "Révision de brouillon attendue." }
                },
                required = new[] { "id", "expectedRevision" }
            }
        ),
        new("delete_script", "Supprime un script partagé et ses versions publiées. Les flows conservent leur copie du code.",
            new { type = "object", properties = new { id = new { type = "string", format = "uuid" } }, required = new[] { "id" } }),
        new("validate_script_draft", "Valide les champs et limites d’un brouillon JavaScript ou Python avant son enregistrement ou sa publication.",
            new { type = "object", properties = new { name = new { type = "string" }, language = new { type = "string", @enum = new[] { "javascript",
                "python" } }, description = new { type = "string" }, code = new { type = "string" } }, required = new[] { "name", "language", "description",
                "code" } }),

        // --- 3b. RESSOURCES PARTAGÉES ---
        new("list_resources", "Liste les ressources partagées et leur dernière version.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }),
        new("get_resource", "Consulte une ressource partagée, ses versions et les fichiers référencés.",
            new { type = "object", properties = new { id = new { type = "string", format = "uuid" } }, required = new[] { "id" } }),
        new("create_resource", "Crée une ressource à partir de fichiers déjà téléversés dans le magasin du serveur.",
            new { type = "object", additionalProperties = false, properties = new { name = new { type = "string", minLength = 1, maxLength = 128 },
                description = new { type = "string", maxLength = 1000 }, files = new { type = "array", minItems = 1, items = new { type = "object",
                additionalProperties = false, properties = new { name = new { type = "string" }, sha256 = new { type = "string" } }, required = new[] { "name",
                "sha256" } } } }, required = new[] { "name", "files" } }),
        new("update_resource", "Modifie le nom et la description d’une ressource sans changer ses versions.",
            new { type = "object", additionalProperties = false, properties = new { id = new { type = "string", format = "uuid" }, name = new { type = "string",
                minLength = 1, maxLength = 128 }, description = new { type = "string", maxLength = 1000 } }, required = new[] { "id", "name",
                "description" } }),
        new("add_resource_version", "Ajoute une version immuable à une ressource à partir de fichiers déjà téléversés.",
            new { type = "object", additionalProperties = false, properties = new { id = new { type = "string", format = "uuid" }, files = new { type = "array",
                minItems = 1, items = new { type = "object", additionalProperties = false, properties = new { name = new { type = "string" },
                sha256 = new { type = "string" } }, required = new[] { "name", "sha256" } } } }, required = new[] { "id", "files" } }),
        new("delete_resource", "Supprime une ressource si aucun flow ne la référence.",
            new { type = "object", properties = new { id = new { type = "string", format = "uuid" } }, required = new[] { "id" } }),

        // --- 4. PROFILS D'ENCODAGE ---
        new(
            "list_encoding_profiles",
            "Liste tous les profils de qualité d'encodage vidéo configurés dans le cluster.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_encoding_profile",
            "Récupère le détail d'un profil d'encodage (cibles par encodeur matériel/logiciel, qualité, presets).",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du profil d'encodage." }
                },
                required = new[] { "id" }
            }
        ),
        new(
            "save_encoding_profile",
            "Crée ou met à jour le brouillon d'un profil d'encodage vidéo. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du profil existant (omettre pour en créer un nouveau)." },
                    name = new { type = "string", description = "Nom du profil d'encodage." },
                    description = new { type = "string", description = "Description du profil." },
                    targets = new
                    {
                        type = "array",
                        maxItems = 64,
                        description = "Liste des cibles d'encodage (encoderId, quality, preset, priority).",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                encoderId = new { type = "string", minLength = 1, maxLength = 128 },
                                quality = new { type = "number", minimum = 0, maximum = 100 },
                                preset = new { type = "string", minLength = 1, maxLength = 128 },
                                priority = new { type = "integer", minimum = 0, maximum = 1000 }
                            },
                            required = new[] { "encoderId", "quality", "preset", "priority" }
                        }
                    },
                    expectedRevision = new { type = "integer", description = "Révision attendue pour mise à jour." }
                },
                required = new[] { "name", "targets" }
            }
        ),
        new(
            "publish_encoding_profile",
            "Publie une version immuable d'un profil d'encodage vidéo. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du profil." },
                    expectedRevision = new { type = "integer", description = "Révision de brouillon attendue." }
                },
                required = new[] { "id", "expectedRevision" }
            }
        ),

        // --- 5. MODÈLES DE FLOWS (TEMPLATES) ---
        new(
            "list_templates",
            "Liste tous les modèles de flows réutilisables du cluster.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_template",
            "Récupère le détail d'un modèle et sa définition de flow complète.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du modèle." }
                },
                required = new[] { "id" }
            }
        ),
        new(
            "create_template_from_flow",
            "Crée un modèle de flow réutilisable à partir d'une version publiée d'un flow existant. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    sourceFlowId = new { type = "string", description = "UUID du flow source." },
                    sourceVersion = new { type = "integer", description = "Numéro de version publiée du flow source." },
                    name = new { type = "string", description = "Nom du modèle créé." },
                    description = new { type = "string", description = "Description du modèle." }
                },
                required = new[] { "sourceFlowId", "sourceVersion", "name" }
            }
        ),

        // --- 6. PACKS DE COMPOSANTS ---
        new(
            "list_component_packs",
            "Liste tous les packs de composants (macro-composants réutilisables) disponibles dans le cluster.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_component_pack",
            "Récupère la définition, entrées et sorties d'un pack de composants.",
            new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", description = "Identifiant UUID du pack de composants." }
                },
                required = new[] { "id" }
            }
        ),
        new(
            "create_component_pack_from_flow",
            "Crée un pack de composants réutilisable à partir d'une séquence de nœuds d'un flow publié. Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    sourceFlowId = new { type = "string", description = "UUID du flow source." },
                    sourceVersion = new { type = "integer", description = "Version publiée du flow." },
                    name = new { type = "string", description = "Nom du pack de composants." },
                    description = new { type = "string", description = "Description du pack." }
                },
                required = new[] { "sourceFlowId", "sourceVersion", "name" }
            }
        ),

        // --- 7. PLUGINS & INTÉGRATIONS EXTERNES ---
        new(
            "list_plugins",
            "Liste les plugins installés (Plex, Sonarr, Radarr, Jellyfin...) avec leurs versions et schémas de configuration.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_plugin_configuration",
            "Récupère la configuration globale d'un plugin (URL, réglages) et la liste des secrets configurés.",
            new
            {
                type = "object",
                properties = new
                {
                    pluginId = new { type = "string", description = "Identifiant du plugin (ex: plex, sonarr, radarr)." }
                },
                required = new[] { "pluginId" }
            }
        ),
        new(
            "save_plugin_configuration",
            "Enregistre la configuration globale d'un plugin (clés API, adresses serveur). Nécessite les droits administrateur.",
            new
            {
                type = "object",
                properties = new
                {
                    pluginId = new { type = "string", description = "Identifiant du plugin." },
                    expectedRevision = new { type = "integer", description = "Révision attendue (défaut 0)." },
                    values = new { type = "object", description = "Dictionnaire clé/valeur des paramètres de configuration." },
                    clearSecrets = new { type = "array", items = new { type = "string" }, description = "Liste facultative de secrets à effacer." }
                },
                required = new[] { "pluginId", "values" }
            }
        ),
        new(
            "test_plugin_connection",
            "Teste la connectivité réseau et les identifiants d'un plugin vers son service externe.",
            new
            {
                type = "object",
                properties = new
                {
                    pluginId = new { type = "string", description = "Identifiant du plugin." },
                    values = new { type = "object", description = "Valeurs de configuration à tester (facultatif, utilise la config enregistrée par défaut)." }
                },
                required = new[] { "pluginId" }
            }
        ),
        new("get_flow_plugin_configuration", "Consulte les valeurs effectives configurées en dérogation pour un plugin sur un flow.",
            new { type = "object", properties = new { flowId = new { type = "string" }, pluginId = new { type = "string" } }, required = new[] { "flowId", "pluginId" } }),
        new("save_flow_plugin_configuration", "Enregistre une dérogation de configuration de plugin pour un flow. Nécessite les droits administrateur.",
            new { type = "object", properties = new { flowId = new { type = "string" }, pluginId = new { type = "string" },
                expectedRevision = new { type = "integer" }, values = new { type = "object" }, clearSecrets = new { type = "array",
                items = new { type = "string" } } }, required = new[] { "flowId", "pluginId", "expectedRevision", "values" } }),

        // --- 8. OUTILS MÉDIA & VALIDATION IA ---
        new(
            "list_media_tools",
            "Liste tous les outils média reconnus par le cluster (FFmpeg, FFprobe, MKVToolNix, MediaInfo, ImageMagick, Python, JS), leurs sous-commandes et leurs rôles.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }
        ),
        new(
            "get_media_tool_info",
            "Fournit la documentation détaillée d'un outil média : sous-commandes, catalogue des encodeurs vidéo/audio (CPU et accélérations matérielles NVENC/QSV/VAAPI), presets et règles d'invocation sécurisées pour l'IA.",
            new
            {
                type = "object",
                properties = new
                {
                    toolId = new { type = "string", description = "Identifiant de l'outil (ex: ffmpeg, ffprobe, mkvtoolnix, mediainfo, imagemagick, python, javascript)." }
                },
                required = new[] { "toolId" }
            }
        ),
        new(
            "validate_tool_definition",
            "Valide un descripteur déclaratif tool.json pour l'intégration sécurisée d'un nouvel outil personnalisé au cluster (architecture 0064).",
            new
            {
                type = "object",
                properties = new
                {
                    definition = new { type = "object", description = "Objet tool.json contenant id, name, executable, probe et capabilitiesExposed." }
                },
                required = new[] { "definition" }
            }
        ),
        new(
            "validate_plugin_manifest",
            "Valide la conformité d'un manifeste plugin.json (namespace strict, runtime Python Landlock, descriptions sémantiques des sorties et schémas) pour l'intégration IA (architectures 0062 et 0063).",
            new
            {
                type = "object",
                properties = new
                {
                    manifest = new { type = "object", description = "Objet plugin.json décrivant le plugin et ses composants." }
                },
                required = new[] { "manifest" }
            }
        )
    ];
}
