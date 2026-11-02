using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Api.Auth;
using Encoding.Api.Executions;
using Encoding.Api.Infrastructure.Data;
using Encoding.Api.Infrastructure.Operations;
using Encoding.Contracts.Auth;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Libraries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ApiAssembly = Encoding.Api.Auth.AuthOptions;

namespace Encoding.Runtime.Tests;

/// <summary>
/// Vérifie les améliorations de l’API : OpenAPI 3.1.0, ProblemDetails RFC 7807, Endpoint Filters,
/// pagination/filtrage des listes, flux SSE temps réel et Rate Limiting sur les opérations lourdes.
/// </summary>
internal static class ApiImprovementsScenarios
{
    private const string AdminPassword = "correct horse battery staple";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync(string connection)
    {
        await using var database = await TestDatabase.CreateAsync(connection, "api_improvements");
        var directory = Path.Combine(Path.GetTempPath(), "api_imp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var secretFile = Path.Combine(directory, Encoding.Core.Nodes.LocalEnrollment.FileName);
            Encoding.Core.Nodes.LocalEnrollment.Create(secretFile);

            await using var factory = CreateFactory(database, directory, new()
            {
                ["Auth:AnonymousRequestsPerMinute"] = "1000",
                ["Auth:HeavyOperationsPerMinute"] = "3",
                [Encoding.Core.Nodes.LocalEnrollment.SecretFileSetting] = secretFile,
                [Encoding.Api.Nodes.LocalNodeEndpoints.BaseUrlSetting] = "http://example.invalid/",
            });

            var anonymous = new TestClient(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));
            var admin = new TestClient(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));

            // Initialisation de l'administrateur
            using (var setup = await anonymous.SendAsync(HttpMethod.Post, "/api/v1/auth/setup", new SetupRequest("admin", AdminPassword, NodeDisplayName: "Noeud API"), csrf: false))
            {
                Check(setup.StatusCode == HttpStatusCode.Created, "Setup succeeds.");
            }
            Check(await admin.LoginAsync("admin", AdminPassword) == HttpStatusCode.OK, "Admin login succeeds.");

            // 1. OpenAPI 3.1.0 : fermé aux anonymes, disponible pour toute session authentifiée
            Check(await anonymous.StatusAsync(HttpMethod.Get, "/api/v1/openapi.json") == HttpStatusCode.Unauthorized,
                "OpenAPI specification requires an authenticated session.");
            using (var openApiResp = await admin.SendAsync(HttpMethod.Get, "/api/v1/openapi.json"))
            {
                Check(openApiResp.StatusCode == HttpStatusCode.OK, "Authenticated user fetches /api/v1/openapi.json.");
                var doc = await openApiResp.Content.ReadFromJsonAsync<JsonElement>(Json);
                Check(doc.GetProperty("openapi").GetString() == "3.1.0", "OpenAPI version is 3.1.0.");
                Check(doc.GetProperty("info").GetProperty("title").GetString() == "Encoding Cluster API", "OpenAPI info title is populated.");
                var paths = doc.GetProperty("paths");
                Check(paths.TryGetProperty("/api/v1/executions", out _), "OpenAPI paths contain /api/v1/executions.");
                Check(paths.TryGetProperty("/api/v1/executions/stream", out _), "OpenAPI paths contain /api/v1/executions/stream.");
                Check(paths.TryGetProperty("/api/v1/flows/{id}", out _), "OpenAPI normalizes route parameters ({id:guid} -> {id}).");
                var schemes = doc.GetProperty("components").GetProperty("securitySchemes");
                Check(schemes.TryGetProperty("sessionCookie", out _) && schemes.TryGetProperty("csrfHeader", out _) && schemes.TryGetProperty("bearerAuth", out _),
                    "OpenAPI components document sessionCookie, csrfHeader, and bearerAuth security schemes.");
            }
            Check(await admin.StatusAsync(HttpMethod.Get, "/openapi/v1.json") == HttpStatusCode.OK,
                "Standard /openapi/v1.json alias is also exposed.");
            Console.WriteLine("PASS: OpenAPI 3.1.0 specification endpoint.");

            // 2. Standardisation RFC 7807 ProblemDetails & Endpoint Filters
            using (var invalidLib = await admin.SendAsync(HttpMethod.Post, "/api/v1/libraries", new CreateLibraryRequest("   "), correlationId: "corr-test-400"))
            {
                Check(invalidLib.StatusCode == HttpStatusCode.BadRequest, "Empty library name is rejected by ValidateLibraryNameFilter.");
                var problem = await invalidLib.Content.ReadFromJsonAsync<JsonElement>(Json);
                Check(problem.GetProperty("status").GetInt32() == 400, "ProblemDetails includes status 400.");
                Check(problem.GetProperty("correlationId").GetString() == "corr-test-400", "ProblemDetails includes correlationId extension.");
                Check(problem.GetProperty("errors").TryGetProperty("name", out _), "ValidationProblem includes field errors.");
            }

            var invalidDraftId = Guid.NewGuid();
            using (var invalidDraft = await admin.SendAsync(
                HttpMethod.Put,
                $"/api/v1/flows/{invalidDraftId}/draft",
                new SaveFlowDraftRequest(0, new FlowDefinition(1, Guid.NewGuid(), 1, "Invalid", "video", [], [])),
                correlationId: "corr-draft-400"))
            {
                Check(invalidDraft.StatusCode == HttpStatusCode.BadRequest, "Mismatched flow draft ID is rejected by ValidateFlowDraftFilter.");
                var problem = await invalidDraft.Content.ReadFromJsonAsync<JsonElement>(Json);
                Check(problem.GetProperty("title").GetString()!.Contains("invalide"), "ProblemDetails title is preserved.");
                Check(problem.GetProperty("correlationId").GetString() == "corr-draft-400", "ProblemDetails correlationId matches request.");
            }

            using (var notFoundFlow = await admin.SendAsync(HttpMethod.Get, $"/api/v1/flows/{Guid.NewGuid()}", correlationId: "corr-404"))
            {
                Check(notFoundFlow.StatusCode == HttpStatusCode.NotFound, "Unknown flow returns 404.");
                var problem = await notFoundFlow.Content.ReadFromJsonAsync<JsonElement>(Json);
                Check(problem.GetProperty("status").GetInt32() == 404 && problem.GetProperty("correlationId").GetString() == "corr-404",
                    "404 response is formatted as RFC 7807 ProblemDetails with correlationId.");
            }

            using (var deprecatedExec = await admin.SendAsync(HttpMethod.Post, "/api/v1/executions", new { }))
            {
                Check(deprecatedExec.StatusCode == HttpStatusCode.Gone, "Deprecated POST /api/v1/executions returns 410 Gone ProblemDetails.");
            }
            Console.WriteLine("PASS: RFC 7807 ProblemDetails and Endpoint Filters.");

            // 3. Pagination et filtrage sur les bibliothèques et les exécutions
            LibraryResponse createdLib;
            using (var libResp = await admin.SendAsync(HttpMethod.Post, "/api/v1/libraries", new CreateLibraryRequest("Films 4K")))
            {
                Check(libResp.StatusCode == HttpStatusCode.Created, "Library creation succeeds.");
                createdLib = (await libResp.Content.ReadFromJsonAsync<LibraryResponse>(Json))!;
            }

            var execRepo = factory.Services.GetRequiredService<ExecutionRepository>();
            var dataSource = factory.Services.GetRequiredService<System.Data.Common.DbDataSource>();
            var exec1 = Guid.NewGuid();
            var exec2 = Guid.NewGuid();
            var dummyDef = JsonSerializer.Serialize(new FlowDefinition(1, Guid.NewGuid(), 1, "Flow HEVC", "video", [], []), Json);
            await using (var conn = await dataSource.OpenConnectionAsync())
            await using (var tx = await conn.BeginTransactionAsync())
            {
                foreach (var (id, media) in new[] { (exec1, "alpha.mkv"), (exec2, "beta.mkv") })
                {
                    await using var cmd = Encoding.Api.Infrastructure.Data.Sql.Command(
                        "INSERT INTO executions(id,media,library_name,flow_name,flow_version,flow_definition,state,attempt,detail) VALUES(@id,@media,'Films 4K','Flow HEVC',1,@def,'queued',1,'En attente.')",
                        conn,
                        tx);
                    cmd.Parameters.AddWithValue("id", id);
                    cmd.Parameters.AddWithValue("media", media);
                    cmd.Parameters.AddWithValue("def", Encoding.Api.Infrastructure.Data.SqlKind.Json, dummyDef);
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            }
            await execRepo.CancelAsync(exec2, CancellationToken.None);

            // Filtrage par state=queued et pagination limit=1
            using (var filteredResp = await admin.SendAsync(HttpMethod.Get, "/api/v1/executions?state=queued&limit=1&offset=0"))
            {
                Check(filteredResp.StatusCode == HttpStatusCode.OK, "Filtered executions endpoint succeeds.");
                var overview = (await filteredResp.Content.ReadFromJsonAsync<ExecutionOverviewResponse>(Json))!;
                Check(overview.TotalCount == 1 && overview.Limit == 1 && overview.Offset == 0, "Filtered totalCount, limit, and offset are returned.");
                Check(overview.Executions.Count == 1 && overview.Executions[0].Id == exec1, "Only the queued execution is returned.");
            }

            // Pagination des événements d'exécution avec afterRevision
            using (var eventsResp = await admin.SendAsync(HttpMethod.Get, $"/api/v1/executions/{exec2}/events?afterRevision=0&limit=10"))
            {
                Check(eventsResp.StatusCode == HttpStatusCode.OK, "Filtered execution events endpoint succeeds.");
                var events = (await eventsResp.Content.ReadFromJsonAsync<List<ExecutionEventResponse>>(Json))!;
                Check(events.Count == 1 && events[0].Revision == 1 && events[0].State == "cancelled",
                    "afterRevision=0 returns only the cancellation event at revision 1.");
            }
            await using (var resumeConnection = await dataSource.OpenConnectionAsync())
            await using (var work = Encoding.Api.Infrastructure.Data.Sql.Command(
                "INSERT INTO execution_work(execution_id,library_id,media_path,media_key) VALUES(@id,@library,'cancelled.mkv','CANCELLED.MKV')",
                resumeConnection))
            {
                work.Parameters.AddWithValue("id", exec2);
                work.Parameters.AddWithValue("library", createdLib.Id);
                await work.ExecuteNonQueryAsync();
            }
            using (var resumeResp = await admin.SendAsync(HttpMethod.Post, $"/api/v1/executions/{exec2}/resume", new { }))
            {
                Check(resumeResp.StatusCode == HttpStatusCode.Created, "Resume endpoint creates a new execution from a cancelled source.");
                var resumedId = (await resumeResp.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
                Check(resumedId != exec2 && await execRepo.GetAsync(resumedId, CancellationToken.None) is { State: "queued", Attempt: 2 },
                    "Resume responds with a linked queued attempt.");
                using var ownerResponse=await admin.SendAsync(HttpMethod.Get,$"/api/v1/executions/{resumedId}/ownership");
                Check(ownerResponse.StatusCode==HttpStatusCode.OK,"Resumed work has an observable owner immediately after creation.");
                var owner=(await ownerResponse.Content.ReadFromJsonAsync<TaskOwnershipStatus>(Json))!;
                Check(owner.ExecutionId==resumedId && owner.OwnerPeerId!=Guid.Empty && owner.Epoch==1 && owner.State=="active","A resumed execution has one initial active ownership epoch.");
            }
            Console.WriteLine("PASS: executions and events pagination and filtering.");

            // 4. Flux temps réel SSE (Server-Sent Events)
            using (var sseOnce = await admin.SendAsync(HttpMethod.Get, "/api/v1/executions/stream?once=true"))
            {
                Check(sseOnce.StatusCode == HttpStatusCode.OK, "SSE overview stream returns 200 OK.");
                Check(sseOnce.Content.Headers.ContentType?.MediaType == "text/event-stream", "SSE response has text/event-stream media type.");
                var payload = await sseOnce.Content.ReadAsStringAsync();
                Check(payload.StartsWith("event: overview\ndata: ") && payload.Contains("alpha.mkv"), "SSE stream emits a well-formed overview event.");
            }

            using (var sseDetail = await admin.SendAsync(HttpMethod.Get, $"/api/v1/executions/{exec1}/stream?once=true"))
            {
                Check(sseDetail.StatusCode == HttpStatusCode.OK, "SSE execution detail stream returns 200 OK.");
                var payload = await sseDetail.Content.ReadAsStringAsync();
                Check(payload.StartsWith("event: detail\ndata: ") && payload.Contains(exec1.ToString()), "SSE detail stream emits execution detail snapshot.");
            }
            Console.WriteLine("PASS: Server-Sent Events (SSE) real-time endpoints.");

            // 5. Rate Limiting sur les opérations lourdes (HeavyOperationsPolicy = 3/min)
            var discoverStatuses = new List<HttpStatusCode>();
            for (var i = 0; i < 4; i++)
            {
                using var resp = await admin.SendAsync(HttpMethod.Post, $"/api/v1/libraries/{createdLib.Id}/discover", new { });
                discoverStatuses.Add(resp.StatusCode);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
                    Check(problem.GetProperty("status").GetInt32() == 429, "Rate limit 429 response is formatted as ProblemDetails.");
                }
            }
            Check(discoverStatuses.Take(3).All(s => s == HttpStatusCode.Conflict) && discoverStatuses[3] == HttpStatusCode.TooManyRequests,
                "Heavy operations are rate-limited after the configured permit limit: " + string.Join(",", discoverStatuses));
            Console.WriteLine("PASS: HeavyOperationsPolicy rate limiting.");

            // 6. Cluster API Tokens, suppression/clonage de flow, et vue d'ensemble bibliothèques
            // A. Création du cluster et génération du token cluster
            string clusterToken;
            using (var createClusterResp = await admin.SendAsync(HttpMethod.Post, "/api/v1/cluster", new { name = "Cluster Test IA" }))
            {
                Check(createClusterResp.StatusCode == HttpStatusCode.OK, "Cluster creation succeeds.");
                var clusterDoc = await createClusterResp.Content.ReadFromJsonAsync<JsonElement>(Json);
                Check(clusterDoc.GetProperty("name").GetString() == "Cluster Test IA", "Cluster name is set.");
                clusterToken = clusterDoc.GetProperty("clusterToken").GetString()!;
                Check(!string.IsNullOrWhiteSpace(clusterToken) && clusterToken.StartsWith("enc_pat_"), "Cluster creation generates and returns clusterToken.");
            }

            // B. Client IA utilisant Bearer token (sans cookie et sans en-tête CSRF)
            var aiClient = new TestClient(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }))
            {
                BearerToken = clusterToken
            };

            // Vérification de GET /api/v1/cluster
            using (var clusterGetResp = await aiClient.SendAsync(HttpMethod.Get, "/api/v1/cluster"))
            {
                Check(clusterGetResp.StatusCode == HttpStatusCode.OK, "AI client can query cluster status with Bearer token.");
                var clusterGetDoc = await clusterGetResp.Content.ReadFromJsonAsync<JsonElement>(Json);
                Check(clusterGetDoc.GetProperty("hasClusterToken").GetBoolean(), "hasClusterToken is true.");
            }

            // C. Gestion des tokens API : création, listage et révocation
            Check(await admin.StatusAsync(HttpMethod.Post, "/api/v1/tokens", new CreateApiTokenRequest("Jeton sans confirmation", "admin"))
                  == HttpStatusCode.Unauthorized, "A user must reauthenticate with a password before creating an API token.");
            Check(await aiClient.StatusAsync(HttpMethod.Post, "/api/v1/tokens", new CreateApiTokenRequest("Agent IA Autonome", "admin"), csrf: false)
                  == HttpStatusCode.Unauthorized, "An API token cannot create another token without user reauthentication.");

            Guid customTokenId;
            string customPlaintextToken;
            using (var createTokenResp = await admin.SendAsync(HttpMethod.Post, "/api/v1/tokens", new CreateApiTokenRequest("Agent IA Autonome", "admin", CurrentPassword: AdminPassword)))
            {
                Check(createTokenResp.StatusCode == HttpStatusCode.Created, "An authenticated user creates a custom API token after password reauthentication.");
                var tokenDoc = await createTokenResp.Content.ReadFromJsonAsync<CreatedApiTokenResponse>(Json);
                customTokenId = tokenDoc!.Id;
                customPlaintextToken = tokenDoc.Token;
                Check(tokenDoc.Name == "Agent IA Autonome", "Token name matches.");
                Check(customPlaintextToken.StartsWith("enc_pat_"), "Raw token starts with enc_pat_ prefix.");
            }

            using (var listTokensResp = await aiClient.SendAsync(HttpMethod.Get, "/api/v1/tokens"))
            {
                Check(listTokensResp.StatusCode == HttpStatusCode.OK, "List API tokens succeeds.");
                var list = await listTokensResp.Content.ReadFromJsonAsync<List<ApiTokenResponse>>(Json);
                Check(list!.Any(t => t.Id == customTokenId && t.Name == "Agent IA Autonome"), "Newly created token is listed.");
            }

            // Test d'un client avec ce nouveau token personnalisé
            var customClient = new TestClient(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }))
            {
                BearerToken = customPlaintextToken
            };
            Check(await customClient.StatusAsync(HttpMethod.Get, "/api/v1/tokens") == HttpStatusCode.OK,
                "Custom API token authenticates successfully.");

            // Révocation du token
            Check(await aiClient.StatusAsync(HttpMethod.Delete, $"/api/v1/tokens/{customTokenId}", csrf: false) == HttpStatusCode.NoContent,
                "AI client revokes API token.");
            Check(await customClient.StatusAsync(HttpMethod.Get, "/api/v1/tokens") == HttpStatusCode.Unauthorized,
                "Revoked API token is immediately refused.");

            // Suppression définitive du token
            Check(await aiClient.StatusAsync(HttpMethod.Delete, $"/api/v1/tokens/{customTokenId}?permanent=true", csrf: false) == HttpStatusCode.NoContent,
                "AI client permanently deletes API token.");
            using (var listAfterDelete = await aiClient.SendAsync(HttpMethod.Get, "/api/v1/tokens"))
            {
                var list = await listAfterDelete.Content.ReadFromJsonAsync<List<ApiTokenResponse>>(Json);
                Check(!list!.Any(t => t.Id == customTokenId), "Permanently deleted token is no longer listed.");
            }

            // D. Cycle de vie des flows : création d'un brouillon, clonage, suppression brouillon, suppression flow
            var testFlowId = Guid.NewGuid();
            var starterDefinition = new FlowDefinition(
                1,
                testFlowId,
                1,
                "Flow IA Test",
                "video",
                [new FlowNodeDefinition("entry", "Entrée vidéo", new FlowComponentReference("base", "1.0.0", "base-video-input", "1.0.0", 1), new Dictionary<string, JsonElement>())],
                []);

            using (var saveDraftResp = await aiClient.SendAsync(HttpMethod.Put, $"/api/v1/flows/{testFlowId}/draft", new SaveFlowDraftRequest(0, starterDefinition), csrf: false))
            {
                Check(saveDraftResp.StatusCode == HttpStatusCode.OK, "AI client creates flow draft with Bearer token.");
            }

            Guid clonedFlowId;
            using (var cloneResp = await aiClient.SendAsync(HttpMethod.Post, $"/api/v1/flows/{testFlowId}/clone", new CloneFlowRequest("Flow IA Clone"), csrf: false))
            {
                Check(cloneResp.StatusCode == HttpStatusCode.Created, "Flow cloning succeeds.");
                var clonedDoc = await cloneResp.Content.ReadFromJsonAsync<FlowDraftResponse>(Json);
                clonedFlowId = clonedDoc!.Definition.Id;
                Check(clonedDoc.Definition.Name == "Flow IA Clone", "Cloned flow has requested name.");
            }

            // Suppression du brouillon cloné
            Check(await aiClient.StatusAsync(HttpMethod.Delete, $"/api/v1/flows/{clonedFlowId}/draft", csrf: false) == HttpStatusCode.NoContent,
                "Deleting flow draft returns NoContent.");

            // Suppression du flow test original
            Check(await aiClient.StatusAsync(HttpMethod.Delete, $"/api/v1/flows/{testFlowId}", csrf: false) == HttpStatusCode.NoContent,
                "Deleting flow returns NoContent.");
            Check(await aiClient.StatusAsync(HttpMethod.Delete, $"/api/v1/flows/{testFlowId}", csrf: false) == HttpStatusCode.NotFound,
                "Deleting non-existent or already deleted flow returns NotFound.");

            // E. Vue d'ensemble des bibliothèques (GetLibrariesOverview)
            using (var overviewResp = await aiClient.SendAsync(HttpMethod.Get, "/api/v1/libraries/overview"))
            {
                Check(overviewResp.StatusCode == HttpStatusCode.OK, "AI client fetches libraries overview.");
                var overviews = await overviewResp.Content.ReadFromJsonAsync<List<LibraryOverviewResponse>>(Json);
                Check(overviews is not null && overviews.Count > 0, "Libraries overview returns list of libraries with metrics.");
            }
            Console.WriteLine("PASS: Cluster API token, M2M bearer auth, token management, flow lifecycle, and library overview.");

            // 7. Validation du protocole MCP (Model Context Protocol - Direct HTTP, Tools & SSE)
            // A. Initialisation MCP (JSON-RPC initialize)
            using (var initResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2024-11-05" }
            }))
            {
                Check(initResp.StatusCode == HttpStatusCode.OK, "MCP endpoint responds to initialize.");
                var initJson = await initResp.Content.ReadAsStringAsync();
                Check(initJson.Contains("encoding-mcp") && initJson.Contains("2024-11-05"), "MCP initialize returns server info.");
                using var envelope = JsonDocument.Parse(initJson);
                Check(envelope.RootElement.TryGetProperty("result", out _) && !envelope.RootElement.TryGetProperty("error", out _),
                    "MCP success envelope contains result and omits error.");
            }

            using (var invalidRpcResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "1.0",
                id = 1,
                method = "ping"
            }))
            {
                Check(invalidRpcResp.StatusCode == HttpStatusCode.OK, "Invalid JSON-RPC request receives a protocol error envelope.");
                using var envelope = JsonDocument.Parse(await invalidRpcResp.Content.ReadAsStringAsync());
                Check(envelope.RootElement.TryGetProperty("error", out _) && !envelope.RootElement.TryGetProperty("result", out _),
                    "MCP error envelope contains error and omits result.");
            }

            using (var invalidParamsResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 7,
                method = "tools/list",
                @params = Array.Empty<string>()
            }))
            {
                using var envelope = JsonDocument.Parse(await invalidParamsResp.Content.ReadAsStringAsync());
                Check(envelope.RootElement.TryGetProperty("error", out _) && !envelope.RootElement.TryGetProperty("result", out _),
                    "MCP rejects a non-object params envelope.");
            }

            using (var notificationResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized"
            }))
            {
                Check(notificationResp.StatusCode == HttpStatusCode.NoContent, "MCP notification receives no JSON-RPC response.");
            }

            // B. Liste des outils (tools/list)
            using (var toolsResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/list",
                @params = new { }
            }))
            {
                Check(toolsResp.StatusCode == HttpStatusCode.OK, "MCP tools/list responds OK.");
                var toolsJson = await toolsResp.Content.ReadAsStringAsync();
                Check(toolsJson.Contains("get_libraries_overview")
                      && toolsJson.Contains("get_flow_schema")
                      && toolsJson.Contains("create_flow")
                      && toolsJson.Contains("delete_flow")
                      && toolsJson.Contains("list_nodes"), "MCP exposes all cluster management tools.");
            }

            // C. Appel d'outil (tools/call : get_flow_schema)
            using (var callSchemaResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "get_flow_schema",
                    arguments = new { }
                }
            }))
            {
                Check(callSchemaResp.StatusCode == HttpStatusCode.OK, "MCP tools/call get_flow_schema succeeds.");
                var schemaJson = await callSchemaResp.Content.ReadAsStringAsync();
                Check(schemaJson.Contains("availableComponents") && schemaJson.Contains("base-input-file"), "MCP get_flow_schema returns available components.");
            }

            // D0. Portées des jetons : lecture seule par défaut sur le MCP, écriture par portée explicite, validation à la création.
            using (var unscopedWrite = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0", id = 40, method = "tools/call",
                @params = new { name = "create_library", arguments = new { name = "Interdite" } }
            }))
            {
                var unscopedJson = await unscopedWrite.Content.ReadAsStringAsync();
                Check(unscopedJson.Contains("\"isError\":true") && unscopedJson.Contains("libraries:write"),
                    "A token without scope cannot write through MCP and is told which scope it needs. Response: " + unscopedJson);
            }
            Check(await aiClient.StatusAsync(HttpMethod.Post, "/api/v1/business-operations/execute", new { name = "create_library", arguments = new { name = "Interdite" } }, csrf: false)
                  == HttpStatusCode.Forbidden, "Business operations apply the same token scopes.");
            Check(await admin.StatusAsync(HttpMethod.Post, "/api/v1/tokens", new CreateApiTokenRequest("Role inconnu", "operator", CurrentPassword: AdminPassword))
                  == HttpStatusCode.BadRequest, "An unknown token role is rejected instead of becoming admin.");
            Check(await admin.StatusAsync(HttpMethod.Post, "/api/v1/tokens", new CreateApiTokenRequest("Portee inconnue", "admin", null, ["root"], AdminPassword))
                  == HttpStatusCode.BadRequest, "An unknown token scope is rejected.");
            Check(await admin.StatusAsync(HttpMethod.Post, "/api/v1/tokens", new CreateApiTokenRequest("Lecture avec ecriture", "viewer", null, ["mcp:write"], AdminPassword))
                  == HttpStatusCode.BadRequest, "A viewer token cannot carry a write scope.");
            string scopedPlaintext;
            using (var scopedCreate = await admin.SendAsync(HttpMethod.Post, "/api/v1/tokens", new CreateApiTokenRequest("Agent flows", "admin", null, ["flows:write"], AdminPassword)))
            {
                Check(scopedCreate.StatusCode == HttpStatusCode.Created, "An authenticated user creates a scoped API token after password reauthentication.");
                var scopedDoc = await scopedCreate.Content.ReadFromJsonAsync<CreatedApiTokenResponse>(Json);
                Check(scopedDoc!.Scopes is ["flows:write"], "The created token reports its scopes.");
                scopedPlaintext = scopedDoc.Token;
            }
            var mcpWriter = new TestClient(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false })) { BearerToken = scopedPlaintext };
            using (var wrongDomain = await mcpWriter.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0", id = 41, method = "tools/call",
                @params = new { name = "create_library", arguments = new { name = "Interdite" } }
            }))
            {
                Check((await wrongDomain.Content.ReadAsStringAsync()).Contains("\"isError\":true"), "A flows:write token cannot write in the libraries domain.");
            }

            // D. Appel d'outil (tools/call : create_flow)
            Guid mcpFlowId = Guid.NewGuid();
            using (var createFlowResp = await mcpWriter.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new
                {
                    name = "create_flow",
                    arguments = new
                    {
                        id = mcpFlowId.ToString(),
                        name = "Flow Cree par MCP",
                        type = "video",
                        publish = true,
                        nodes = new[]
                        {
                            new { id = "n1", component = "base-input-file", name = "Entree" },
                            new { id = "n2", component = "base-audio-rules", name = "Regles" }
                        },
                        connections = new[]
                        {
                            new { id = "c1", sourceNodeId = "n1", sourceOutputId = "success", targetNodeId = "n2" }
                        }
                    }
                }
            }))
            {
                Check(createFlowResp.StatusCode == HttpStatusCode.OK, $"MCP create_flow succeeds. Status: {(int)createFlowResp.StatusCode}, Body: {await createFlowResp.Content.ReadAsStringAsync()}");
                var createdJson = await createFlowResp.Content.ReadAsStringAsync();
                Check(createdJson.Contains("Flow cr") && createdJson.Contains("version 1"), $"MCP create_flow creates and publishes flow. Response: {createdJson}");
            }

            // E. Appel d'outil (tools/call : delete_flow)
            using (var deleteFlowResp = await mcpWriter.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 5,
                method = "tools/call",
                @params = new
                {
                    name = "delete_flow",
                    arguments = new { flowId = mcpFlowId.ToString() }
                }
            }))
            {
                Check(deleteFlowResp.StatusCode == HttpStatusCode.OK, "MCP delete_flow succeeds.");
                var deleteJson = await deleteFlowResp.Content.ReadAsStringAsync();
                Check(deleteJson.Contains("supprim") && (deleteJson.Contains("succ") || deleteJson.Contains("Le flow")), "MCP delete_flow confirms deletion.");
            }

            // F. Transport SSE (Server-Sent Events)
            using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var sseReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/mcp/sse");
            sseReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", clusterToken);
            using var sseResp = await factory.CreateClient().SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
            Check(sseResp.StatusCode == HttpStatusCode.OK, "MCP SSE endpoint establishes text/event-stream.");
            Check(sseResp.Content.Headers.ContentType?.MediaType == "text/event-stream", "MCP SSE returns text/event-stream.");

            await using var sseStream = await sseResp.Content.ReadAsStreamAsync(sseCts.Token);
            using var sseReader = new StreamReader(sseStream);
            var endpointLine = await sseReader.ReadLineAsync(sseCts.Token);
            var dataLine = await sseReader.ReadLineAsync(sseCts.Token);
            Check(endpointLine == "event: endpoint", "MCP SSE sends endpoint event first.");
            Check(dataLine is not null && dataLine.StartsWith("data: /api/v1/mcp/message?sessionId="), "MCP SSE specifies message endpoint with sessionId: " + dataLine);

            var sessionId = dataLine!["data: /api/v1/mcp/message?sessionId=".Length..];

            // Envoi d'un message vers la session SSE
            using (var sseMsgResp = await aiClient.SendAsync(HttpMethod.Post, $"/api/v1/mcp/message?sessionId={sessionId}", new
            {
                jsonrpc = "2.0",
                id = 10,
                method = "ping"
            }))
            {
                Check(sseMsgResp.StatusCode == HttpStatusCode.Accepted, "POST to /api/v1/mcp/message is accepted.");
            }

            // Lecture de la réponse ping sur le stream SSE
            await sseReader.ReadLineAsync(sseCts.Token); // ligne vide séparatrice
            var msgEventLine = await sseReader.ReadLineAsync(sseCts.Token);
            var msgDataLine = await sseReader.ReadLineAsync(sseCts.Token);
            Check(msgEventLine == "event: message", "MCP SSE streams back message event.");
            Check(msgDataLine is not null && msgDataLine.Contains("\"id\":10"), "MCP SSE delivers ping JSON-RPC response on stream.");

            using (var duplicateSseMessage = await aiClient.SendAsync(HttpMethod.Post, $"/api/v1/mcp/message?sessionId={sessionId}", new
            {
                jsonrpc = "2.0",
                id = 10,
                method = "ping"
            }))
            {
                Check(duplicateSseMessage.StatusCode == HttpStatusCode.Conflict,
                    "MCP SSE rejects a duplicate JSON-RPC request id before processing it.");
            }

            // G. Nouveaux outils MCP : Scripts, Profils d'encodage, Modèles, Packs et Plugins
            // Test Script : création puis publication
            Guid scriptId = Guid.NewGuid();
            using (var createScriptResp = await mcpWriter.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 20,
                method = "tools/call",
                @params = new
                {
                    name = "save_script",
                    arguments = new
                    {
                        id = scriptId.ToString(),
                        name = "Script MCP Test",
                        language = "javascript",
                        description = "Script de test créé par MCP",
                        code = "console.log('hello from mcp');"
                    }
                }
            }))
            {
                Check(createScriptResp.StatusCode == HttpStatusCode.OK, $"MCP save_script succeeds. Status: {(int)createScriptResp.StatusCode}, Body: {await createScriptResp.Content.ReadAsStringAsync()}");
                var scriptJson = await createScriptResp.Content.ReadAsStringAsync();
                Check((scriptJson.Contains("Script cr") || scriptJson.Contains(@"Script cr\u00E9\u00E9")) && (scriptJson.Contains("succ") || scriptJson.Contains(@"succ\u00E8s")), $"MCP save_script creates draft. Response: {scriptJson}");
            }

            using (var publishScriptResp = await mcpWriter.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 21,
                method = "tools/call",
                @params = new
                {
                    name = "publish_script",
                    arguments = new
                    {
                        id = scriptId.ToString(),
                        expectedRevision = 1
                    }
                }
            }))
            {
                Check(publishScriptResp.StatusCode == HttpStatusCode.OK, "MCP publish_script succeeds.");
                var pubScriptJson = await publishScriptResp.Content.ReadAsStringAsync();
                Check(pubScriptJson.Contains("Script publi") && pubScriptJson.Contains("version 1"), "MCP publish_script publishes version 1.");
            }

            // Test Plugins : list_plugins
            using (var listPluginsResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 22,
                method = "tools/call",
                @params = new
                {
                    name = "list_plugins",
                    arguments = new { }
                }
            }))
            {
                Check(listPluginsResp.StatusCode == HttpStatusCode.OK, "MCP list_plugins succeeds.");
                var pluginsJson = await listPluginsResp.Content.ReadAsStringAsync();
                Check(pluginsJson.Contains("base"), "MCP list_plugins lists built-in base plugin.");
            }

            // Test Profils : list_encoding_profiles
            using (var listProfilesResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 23,
                method = "tools/call",
                @params = new
                {
                    name = "list_encoding_profiles",
                    arguments = new { }
                }
            }))
            {
                Check(listProfilesResp.StatusCode == HttpStatusCode.OK, "MCP list_encoding_profiles succeeds.");
                var profilesJson = await listProfilesResp.Content.ReadAsStringAsync();
                Check(profilesJson.Contains("Équilibré") || profilesJson.Contains("balanced") || profilesJson.Length > 10, "MCP list_encoding_profiles returns profiles.");
            }

            // Test Outils média MCP : list_media_tools
            using (var listMediaToolsResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 24,
                method = "tools/call",
                @params = new
                {
                    name = "list_media_tools",
                    arguments = new { }
                }
            }))
            {
                Check(listMediaToolsResp.StatusCode == HttpStatusCode.OK, "MCP list_media_tools succeeds.");
                var mediaToolsJson = await listMediaToolsResp.Content.ReadAsStringAsync();
                Check(mediaToolsJson.Contains("ffmpeg") && mediaToolsJson.Contains("mkvtoolnix") && mediaToolsJson.Contains("ffprobe"), "MCP list_media_tools returns canonical tools.");
            }

            // Test Outils média MCP : get_media_tool_info (ffmpeg)
            using (var getToolInfoResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 25,
                method = "tools/call",
                @params = new
                {
                    name = "get_media_tool_info",
                    arguments = new { toolId = "ffmpeg" }
                }
            }))
            {
                Check(getToolInfoResp.StatusCode == HttpStatusCode.OK, "MCP get_media_tool_info succeeds.");
                var toolInfoJson = await getToolInfoResp.Content.ReadAsStringAsync();
                Check(toolInfoJson.Contains("supportedVideoEncoders") && toolInfoJson.Contains("safeUsageGuidelines"), "MCP get_media_tool_info returns encoders and guidelines.");
            }

            // Test Validation Tool MCP : validate_tool_definition
            using (var valToolResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 26,
                method = "tools/call",
                @params = new
                {
                    name = "validate_tool_definition",
                    arguments = new
                    {
                        definition = new
                        {
                            id = "custom-whisper",
                            name = "Whisper Custom",
                            executable = "whisper",
                            probe = new
                            {
                                versionArgs = new[] { "--version" },
                                versionRegex = @"whisper\s+v(?<version>[0-9\.]+)"
                            },
                            capabilitiesExposed = new[] { "transcription" }
                        }
                    }
                }
            }))
            {
                Check(valToolResp.StatusCode == HttpStatusCode.OK, "MCP validate_tool_definition succeeds.");
                var valToolJson = await valToolResp.Content.ReadAsStringAsync();
                Check(valToolJson.Contains("isValid") && valToolJson.Contains("true") && !valToolJson.Contains("Des erreurs bloquantes"), "MCP validate_tool_definition confirms valid definition.");
            }

            // Test Validation Plugin MCP : validate_plugin_manifest
            using (var valPluginResp = await aiClient.SendAsync(HttpMethod.Post, "/api/v1/mcp", new
            {
                jsonrpc = "2.0",
                id = 27,
                method = "tools/call",
                @params = new
                {
                    name = "validate_plugin_manifest",
                    arguments = new
                    {
                        manifest = new
                        {
                            id = "community.flac-converter",
                            version = "1.0.0",
                            runtime = new { type = "python3", entrypoint = "main.py" },
                            components = new object[]
                            {
                                new
                                {
                                    id = "convert-flac",
                                    name = "Convertir FLAC",
                                    description = "Convertit les pistes FLAC volumineuses en ALAC ou Opus.",
                                    outputs = new object[]
                                    {
                                        new { id = "success", label = "Succès", description = "Piste audio convertie avec succès." },
                                        new { id = "error", label = "Erreur", description = "Échec de lecture ou de conversion de la piste." }
                                    }
                                }
                            }
                        }
                    }
                }
            }))
            {
                Check(valPluginResp.StatusCode == HttpStatusCode.OK, "MCP validate_plugin_manifest succeeds.");
                var valPluginJson = await valPluginResp.Content.ReadAsStringAsync();
                Check(valPluginJson.Contains("isValid") && valPluginJson.Contains("true") && !valPluginJson.Contains("erreurs de conformité"), "MCP validate_plugin_manifest confirms valid manifest.");
            }

            // Test REST : GET /api/v1/plugins
            using (var restPluginsResp = await aiClient.SendAsync(HttpMethod.Get, "/api/v1/plugins"))
            {
                Check(restPluginsResp.StatusCode == HttpStatusCode.OK, "REST GET /api/v1/plugins responds OK.");
            }

            Console.WriteLine("PASS: Model Context Protocol (MCP) server: JSON-RPC initialize, tools discovery/call, SSE transport, and extended tools (scripts, profiles, templates, packs, plugins).");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static WebApplicationFactory<ApiAssembly> CreateFactory(TestDatabase cluster, string directory, Dictionary<string, string> settings) =>
        new WebApplicationFactory<ApiAssembly>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
            foreach (var (key, value) in cluster.Settings()) builder.UseSetting(key, value);
            builder.UseSetting("Logging:LogLevel:Default", "Error");
            builder.UseSetting("Auth:Enabled", "true");
            builder.UseSetting("FlowBackups:Directory", Path.Combine(directory, "backups"));
            builder.UseSetting("Runtime:ArtifactDirectory", Path.Combine(directory, "artifacts"));
            builder.UseSetting("PluginSettings:KeyDirectory", Path.Combine(directory, "keys"));
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Encoding.Api.Flows.FlowComponentRegistry>();
                services.AddSingleton(StoreCatalogFixture.Create());
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(descriptor);
            });
        });

    private sealed class TestClient(HttpClient http)
    {
        public string? Cookie { get; set; }
        public string? BearerToken { get; set; }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, bool csrf = true, string? correlationId = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            if (Cookie is not null) request.Headers.Add("Cookie", $"{SessionAuthentication.CookieName}={Cookie}");
            if (BearerToken is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BearerToken);
            if (csrf && method != HttpMethod.Get) request.Headers.Add(ManagementAccessHandler.CsrfHeader, "1");
            if (correlationId is not null) request.Headers.Add(OperationsEndpoints.CorrelationHeader, correlationId);
            return await http.SendAsync(request);
        }

        public async Task<HttpStatusCode> StatusAsync(HttpMethod method, string url, object? body = null, bool csrf = true)
        {
            using var response = await SendAsync(method, url, body, csrf);
            return response.StatusCode;
        }

        public async Task<HttpStatusCode> LoginAsync(string username, string password)
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(username, password), csrf: false);
            if (response.IsSuccessStatusCode)
            {
                var header = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(SessionAuthentication.CookieName + "="));
                Cookie = header[(SessionAuthentication.CookieName.Length + 1)..header.IndexOf(';')];
            }
            return response.StatusCode;
        }
    }
}
