using System.Net.Http.Headers;
using System.Text.Json;

namespace Encoding.Api.Mcp;

/// <summary>
/// Commande console permettant d'exécuter un pont MCP via stdio (standard input/output).
/// Utilisée par les assistants IA exécutés localement ou via 'docker exec -i ...'.
/// </summary>
public static class McpCommand
{
    public const string Name = "mcp";

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, CancellationToken ct)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i].StartsWith("--"))
                options[args[i][2..]] = args[i + 1];
        }

        var baseUrl = options.GetValueOrDefault("url")
            ?? Environment.GetEnvironmentVariable("ENCODING_API_URL")
            ?? "http://127.0.0.1:5000";

        var token = options.GetValueOrDefault("token")
            ?? Environment.GetEnvironmentVariable("ENCODING_API_TOKEN");

        using var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2)
        };

        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync();
            if (line is null) break; // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "api/v1/mcp")
                {
                    Content = new StringContent(line, System.Text.Encoding.UTF8, "application/json")
                };

                using var response = await http.SendAsync(requestMessage, ct);
                if (response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                        continue;

                    var responseBody = await response.Content.ReadAsStringAsync(ct);
                    if (!string.IsNullOrWhiteSpace(responseBody))
                    {
                        await output.WriteLineAsync(responseBody);
                        await output.FlushAsync();
                    }
                }
                else
                {
                    // Erreur HTTP traduite en réponse JSON-RPC
                    var errorResponse = new
                    {
                        jsonrpc = "2.0",
                        id = (object?)null,
                        error = new
                        {
                            code = (int)response.StatusCode,
                            message = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                                ? "Authentification requise : veuillez fournir le jeton API via la variable ENCODING_API_TOKEN ou le paramètre --token."
                                : $"Erreur HTTP du serveur Encoding ({response.StatusCode})."
                        }
                    };
                    await output.WriteLineAsync(JsonSerializer.Serialize(errorResponse));
                    await output.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                var errorResponse = new
                {
                    jsonrpc = "2.0",
                    id = (object?)null,
                    error = new
                    {
                        code = -32603,
                        message = $"Impossible de contacter l'API Encoding sur {baseUrl} : {ex.Message}"
                    }
                };
                await output.WriteLineAsync(JsonSerializer.Serialize(errorResponse));
                await output.FlushAsync();
            }
        }

        return 0;
    }
}
