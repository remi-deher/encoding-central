using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Encoding.Api.Auth;
using Encoding.Api.Infrastructure;
using Encoding.Api.Infrastructure.Data;
using Encoding.Contracts.Auth;
using Encoding.Contracts.Workers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection;
// Encoding.Api et Encoding.WorkerAgent exposent chacun un Program : un type de l’assemblage API désigne le point d’entrée à héberger.
using ApiAssembly = Encoding.Api.Auth.AuthOptions;

namespace Encoding.Runtime.Tests;

/// <summary>Authentification, rôles et durcissement du jumelage, vérifiés sur le vrai pipeline HTTP de l’API.</summary>
internal static class AuthScenarios
{
    private const string AdminPassword = "correct horse battery staple";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void RunUnit()
    {
        var hash = PasswordHasher.Hash("un mot de passe solide");
        Check(hash.StartsWith("pbkdf2-sha512$") && !hash.Contains("solide"), "A password hash never contains the password.");
        Check(PasswordHasher.Hash("un mot de passe solide") != hash, "Each hash uses a fresh salt.");
        Check(PasswordHasher.Verify("un mot de passe solide", hash, out var rehash) && !rehash, "The right password verifies at the current cost.");
        Check(!PasswordHasher.Verify("un autre mot de passe", hash, out _), "A wrong password is rejected.");
        Check(!PasswordHasher.Verify("x", "not-a-hash", out _) && !PasswordHasher.Verify("x", "pbkdf2-sha512$1$@@@$@@@", out _), "Malformed stored hashes never verify or throw.");
        var weak = PasswordHasher.Verify("x", "pbkdf2-sha512$1000$" + Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("x", new byte[16], 1000, System.Security.Cryptography.HashAlgorithmName.SHA512, 32)), out var upgrade);
        Check(weak && upgrade, "A hash below the current cost verifies and asks for a rehash.");
        Check(AuthService.ValidateNewUser("alice", "douze caracteres").Count == 0, "A valid account passes the policy.");
        Check(AuthService.ValidateNewUser("a b", "court").Count == 2, "Spaces in names and short passwords are both reported.");
        Check(AuthService.ValidatePassword("p", new string('x', 257)).Count == 1, "Oversized passwords are refused before hashing.");
        Console.WriteLine("PASS: password hashing, rehash detection and credential policy.");
        var matcher = ProxyMatcher.Create(["192.168.1.10", "172.18.0.0/16", "::1"]);
        Check(matcher.Contains(System.Net.IPAddress.Parse("192.168.1.10")) && !matcher.Contains(System.Net.IPAddress.Parse("192.168.1.11")), "A single proxy address matches only itself.");
        Check(matcher.Contains(System.Net.IPAddress.Parse("172.18.5.9")) && matcher.Contains(System.Net.IPAddress.Parse("::ffff:192.168.1.10")), "Networks and IPv4-mapped addresses match.");
        Check(!ProxyMatcher.IsValidEntry("pas une adresse") && !ProxyMatcher.IsValidEntry("10.0.0.0/40"), "Malformed proxy entries are refused.");
        var settingsFile = Path.Combine(Path.GetTempPath(), "network_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new NetworkSettingsService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Network:SettingsFile"] = settingsFile }).Build());
            Check(!service.IsTrustedProxy(System.Net.IPAddress.Parse("192.168.1.10")), "No proxy is trusted by default.");
            Check(service.Save(true, []).Count == 1, "Trust cannot be enabled without a proxy address.");
            Check(service.Save(true, ["bad"]).Count == 1, "An invalid proxy address is refused.");
            Check(service.Save(true, ["192.168.1.10"]).Count == 0 && service.IsTrustedProxy(System.Net.IPAddress.Parse("192.168.1.10")), "A declared proxy is trusted after saving.");
            Check(service.Save(true, ["192.168.1.10"], "encoding.example.com").ContainsKey("publicUrl")
                && service.Save(true, ["192.168.1.10"], "ftp://encoding.example.com").ContainsKey("publicUrl")
                && service.Save(true, ["192.168.1.10"], "https://user:pass@encoding.example.com").ContainsKey("publicUrl")
                && service.Save(true, ["192.168.1.10"], "https://encoding.example.com/?a=1").ContainsKey("publicUrl"), "An incomplete or unsafe public URL is refused.");
            Check(service.Save(true, ["192.168.1.10"], " https://encoding.example.com/ ").Count == 0 && service.Current.PublicUrl == "https://encoding.example.com", "A public URL is trimmed and loses its trailing slash.");
            var reloaded = new NetworkSettingsService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Network:SettingsFile"] = settingsFile }).Build());
            Check(reloaded.IsTrustedProxy(System.Net.IPAddress.Parse("192.168.1.10")), "The proxy setting survives a restart.");
            Check(reloaded.Current.PublicUrl == "https://encoding.example.com", "The public URL survives a restart.");
            Check(service.Save(true, ["192.168.1.10"], "").Count == 0 && service.Current.PublicUrl is null, "An empty public URL clears the setting.");
        }
        finally { File.Delete(settingsFile); }
        Console.WriteLine("PASS: trusted proxy matching and settings persistence.");
        SetupNodeUnit();
    }

    private static void SetupNodeUnit()
    {
        var addresses = LocalNetworkAddresses.Select([
            ("eth0", System.Net.IPAddress.Parse("192.168.1.20")),
            ("eth0", System.Net.IPAddress.Parse("192.168.1.20")),
            ("lo", System.Net.IPAddress.Loopback),
            ("eth1", System.Net.IPAddress.Parse("169.254.10.2")),
            ("docker0", System.Net.IPAddress.Parse("172.17.0.1")),
            ("br-3f2a", System.Net.IPAddress.Parse("172.18.0.1")),
            ("eth0", System.Net.IPAddress.Parse("fe80::1")),
            ("eth1", System.Net.IPAddress.Parse("192.168.1.20")),
            ("lo", System.Net.IPAddress.IPv6Loopback),
            ("veth0", System.Net.IPAddress.Parse("fe80::2%7")),
            ("wlan0", System.Net.IPAddress.Parse("10.0.0.5")),
        ]);
        Check(addresses.Select(item => item.Interface + "=" + item.Address).SequenceEqual([
            "br-3f2a=172.18.0.1", "docker0=172.17.0.1", "eth0=192.168.1.20", "eth0=fe80::1",
            "eth1=169.254.10.2", "eth1=192.168.1.20", "lo=127.0.0.1", "lo=::1", "veth0=fe80::2%7", "wlan0=10.0.0.5"]),
            "All assigned IPv4/IPv6 addresses retain their interface, including loopback, virtual and scoped addresses; only identical pairs are deduplicated.");
        var manyAddresses = Enumerable.Range(1, 40).Select(index => ("eth0", System.Net.IPAddress.Parse("10.1.0." + index)));
        Check(LocalNetworkAddresses.Select(manyAddresses).Count == 40, "A host with many assigned addresses is not truncated to sixteen entries.");
        var urls = Encoding.Api.Workers.SetupWorkerProbe.Urls("https://encoding.example.com/", "http://encoding:8080/", "http://127.0.0.1:5080/", "https://ENCODING.example.com", "http://192.168.1.20:8080/", null, "pas une url");
        Check(urls.SequenceEqual(["https://encoding.example.com", "http://192.168.1.20:8080"]),
            "Only network-reachable configured URLs are shown, without Docker service names nor loopback: " + string.Join(",", urls));
        const string agent = """
            {"nodeId":"2b1c","name":"encoding-node","displayName":"Salon","roles":["server","worker"],"pairingKey":"SECRET-KEY","verificationCode":"123456",
             "clusterId":"7d0f","clusterName":"Maison","isEnrolled":true,
             "inventory":{"operatingSystem":"Ubuntu 24.04","architecture":"X64","processorCount":8,"memoryMb":16384,"processorModel":"Intel Core i5","gpus":[{"name":"Intel UHD Graphics 630","vendor":"Intel","driver":"i915","device":"card0"}],"fontSets":["noto"]},
             "capabilities":[{"toolId":"ffmpeg","version":"7.1","mediaTypes":["video"],"accelerationTypes":["cpu","qsv"],"encoders":["hevc_qsv"],"encoderChecks":[{"encoderId":"hevc_qsv","status":"usable"}],"manifestFingerprint":"abc"}]}
            """;
        var sheet = Encoding.Api.Workers.SetupWorkerProbe.ProjectAgentStatus(agent, addresses, urls);
        var serialized = JsonSerializer.Serialize(sheet, Json);
        Check(sheet.AgentAvailable && sheet.DisplayName == "Salon" && sheet.Inventory?.ProcessorModel == "Intel Core i5" && sheet.Inventory.Gpus?.Single().Name == "Intel UHD Graphics 630",
            "The setup sheet keeps the hardware read by the local agent.");
        Check(sheet.Tools?.Single() is { ToolId: "ffmpeg" } tool && tool.AccelerationTypes.Contains("qsv") && tool.EncoderChecks.Single().Status == "usable",
            "The setup sheet keeps what is needed to show hardware acceleration.");
        Check(!serialized.Contains("SECRET-KEY") && !serialized.Contains("123456") && !serialized.Contains("7d0f") && !serialized.Contains("Maison")
            && !serialized.Contains("noto") && !serialized.Contains("abc") && !serialized.Contains("7.1"),
            "The setup sheet is an allow-list: no pairing secret, cluster identity, fonts, tool versions nor fingerprints.");
        Check(!Encoding.Api.Workers.SetupWorkerProbe.ProjectAgentStatus("not json", [], []).AgentAvailable, "An unreadable agent answer means unknown hardware.");
        Console.WriteLine("PASS: setup node sheet allow-list, network addresses and configured URLs.");
    }

    private static async Task SecurityHeadersAsync(WebApplicationFactory<ApiAssembly> factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/auth/status");
        Check(response.Headers.TryGetValues("X-Content-Type-Options", out var sniff) && sniff.Single() == "nosniff", "API responses forbid content sniffing.");
        Check(response.Headers.Contains("X-Frame-Options") && response.Headers.Contains("Referrer-Policy"), "API responses carry framing and referrer protections.");
        Check(response.Headers.CacheControl?.NoStore == true, "API responses are never cached.");
        Check(!response.Headers.Contains("Strict-Transport-Security"), "HSTS is only sent over HTTPS.");
        Console.WriteLine("PASS: security headers on API responses.");
    }

    public static async Task RunAsync(string connection)
    {
        await using var database = await TestDatabase.CreateAsync(connection, "auth");
        var directory = Path.Combine(Path.GetTempPath(), "auth_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cluster = database;
            var secretFile = Path.Combine(directory, Encoding.Core.Workers.LocalEnrollment.FileName);
            Encoding.Core.Workers.LocalEnrollment.Create(secretFile);
            LocalSecret = File.ReadAllText(secretFile).Trim();
            await using (var factory = CreateFactory(cluster, directory, new() { ["Auth:AnonymousRequestsPerMinute"] = "1000", ["Auth:MaxPendingPairingRequests"] = "2", [Encoding.Core.Workers.LocalEnrollment.SecretFileSetting] = secretFile, [Encoding.Api.Workers.LocalWorkerEndpoints.BaseUrlSetting] = "http://example.invalid/" }))
            {
                await ScenarioAsync(factory);
                await EndpointCatalogueAsync(factory);
                await SecurityHeadersAsync(factory);
            }
            await using (var limited = CreateFactory(cluster, directory, new() { ["Auth:AnonymousRequestsPerMinute"] = "3" }))
            {
                using var client = limited.CreateClient();
                var statuses = new List<HttpStatusCode>();
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody", "mauvais mot de passe"));
                    statuses.Add(response.StatusCode);
                }
                Check(statuses.Take(3).All(status => status == HttpStatusCode.Unauthorized) && statuses.Skip(3).All(status => status == HttpStatusCode.TooManyRequests),
                    "Anonymous protocol routes are rate limited per source address: " + string.Join(",", statuses));
                Console.WriteLine("PASS: anonymous login and pairing routes are rate limited.");
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static WebApplicationFactory<ApiAssembly> CreateFactory(TestDatabase cluster, string directory, Dictionary<string, string> settings) =>
        new WebApplicationFactory<ApiAssembly>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.ContentRootKey, AppContext.BaseDirectory);
            foreach (var (key, value) in cluster.Settings()) builder.UseSetting(key, value);
            builder.UseSetting("Logging:LogLevel:Default", "Error");
            builder.UseSetting("Auth:Enabled", "true");
            builder.UseSetting("FlowBackups:Directory", Path.Combine(directory, "backups"));
            builder.UseSetting("Runtime:ArtifactDirectory", Path.Combine(directory, "artifacts"));
            builder.UseSetting("PluginSettings:KeyDirectory", Path.Combine(directory, "keys"));
            builder.UseSetting("AiTools:CredentialsDirectory", Path.Combine(directory, "ai-tools"));
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            // Les tâches de fond (mDNS, surveillance, sauvegardes) ne participent pas à ces scénarios.
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)).ToArray()) services.Remove(descriptor);
            });
        });

    private sealed class Client(HttpClient http)
    {
        public string? Cookie { get; set; }
        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, bool csrf = true, string? nodeToken = null, string? localSecret = null)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            if (Cookie is not null) request.Headers.Add("Cookie", $"{SessionAuthentication.CookieName}={Cookie}");
            if (csrf && method != HttpMethod.Get) request.Headers.Add(ManagementAccessHandler.CsrfHeader, "1");
            if (nodeToken is not null) request.Headers.Add("X-Node-Token", nodeToken);
            if (localSecret is not null) request.Headers.Add(Encoding.Core.Workers.LocalEnrollment.HeaderName, localSecret);
            return await http.SendAsync(request);
        }
        public async Task<HttpStatusCode> StatusAsync(HttpMethod method, string url, object? body = null, bool csrf = true, string? nodeToken = null, string? localSecret = null)
        {
            using var response = await SendAsync(method, url, body, csrf, nodeToken, localSecret);
            return response.StatusCode;
        }
        public async Task<HttpStatusCode> LoginAsync(string username, string password)
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(username, password), csrf: false);
            if (response.IsSuccessStatusCode) Cookie = ReadCookie(response);
            return response.StatusCode;
        }
    }

    private static string ReadCookie(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(SessionAuthentication.CookieName + "="));
        var lower = header.ToLowerInvariant();
        Check(lower.Contains("httponly") && lower.Contains("samesite=strict") && lower.Contains("path=/"), "The session cookie is HttpOnly, SameSite=Strict and scoped to the site: " + header);
        return header[(SessionAuthentication.CookieName.Length + 1)..header.IndexOf(';')];
    }

    private static WorkerEnrollmentRequest NewWorker(Guid id, string token) =>
        new(id, "worker-" + id.ToString("N")[..6], [WorkerRole.Worker], 1, [new("ffprobe", "7.0", ["video"], [])], token, Version: AppVersion.Current);

    private static string LocalSecret = string.Empty;

    private static string NodeToken() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    private static async Task ScenarioAsync(WebApplicationFactory<ApiAssembly> factory)
    {
        var anonymous = new Client(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));
        var admin = new Client(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));
        var viewer = new Client(factory.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false }));
        var get = HttpMethod.Get; var post = HttpMethod.Post; var put = HttpMethod.Put; var delete = HttpMethod.Delete;

        // Avant initialisation et sans session : tout est fermé sauf le protocole public.
        using (var status = await anonymous.SendAsync(get, "/api/v1/auth/status"))
        {
            var body = (await status.Content.ReadFromJsonAsync<AuthStatusResponse>(Json))!;
            Check(body.SetupRequired && body.User is null, "A fresh cluster reports that setup is required.");
        }
        Check(await anonymous.StatusAsync(get, "/health/live") == HttpStatusCode.OK, "Liveness stays public.");
        Check(await anonymous.StatusAsync(get, "/api/v1/central") == HttpStatusCode.OK, "Cluster identity stays public for worker discovery.");
        foreach (var url in new[] { "/api/v1/system/version", "/api/v1/system/updates", "/api/v1/tools/ai/quotas", "/api/v1/tools/ai/sessions", "/api/v1/libraries", "/api/v1/flows", "/api/v1/workers", "/api/v1/executions", "/api/v1/plugins/base/configuration", "/api/v1/users", "/api/v1/workers/enrollment-requests", "/api/v1/local-worker/status" })
            Check(await anonymous.StatusAsync(get, url) == HttpStatusCode.Unauthorized, "Anonymous reads of " + url + " are refused.");
        Check(await anonymous.StatusAsync(post, "/api/v1/libraries", new { name = "x" }) == HttpStatusCode.Unauthorized, "Anonymous writes are refused.");
        // Fiche du worker pour l'assistant : lisible sans compte tant qu'aucun admin n'existe, sans aucun secret.
        using (var setupNode = await anonymous.SendAsync(get, "/api/v1/auth/setup/worker"))
        {
            Check(setupNode.StatusCode == HttpStatusCode.OK, "The setup wizard reads the local node sheet before any account exists.");
            var text = await setupNode.Content.ReadAsStringAsync();
            var body = JsonSerializer.Deserialize<SetupWorkerResponse>(text, Json)!;
            Check(!body.AgentAvailable && body.Inventory is null && body.Tools is null,
                "Without a reachable local agent, hardware is reported unknown rather than absent.");
            Check(!text.Contains("pairingKey") && !text.Contains("verificationCode") && !text.Contains("clusterId"),
                "The setup node sheet never carries pairing secrets nor cluster identifiers.");
        }

        // Initialisation du premier administrateur.
        Check(await anonymous.StatusAsync(post, "/api/v1/auth/setup", new SetupRequest("root", "court"), csrf: false) == HttpStatusCode.BadRequest, "Setup enforces the password policy when credentials are provided.");
        using (var setup = await anonymous.SendAsync(post, "/api/v1/auth/setup", new SetupRequest("root", AdminPassword, WorkerDisplayName: "  Serveur du salon  "), csrf: false))
        {
            Check(setup.StatusCode == HttpStatusCode.Created, "Setup creates the first administrator.");
            admin.Cookie = ReadCookie(setup);
            Check((await setup.Content.ReadFromJsonAsync<CurrentUserResponse>(Json))!.Role == UserRole.Admin, "The first account is an administrator.");
        }
        Check(await anonymous.StatusAsync(post, "/api/v1/auth/setup", new SetupRequest("autre", AdminPassword), csrf: false) == HttpStatusCode.Conflict, "Setup cannot run twice.");
        Check(await anonymous.StatusAsync(get, "/api/v1/auth/setup/worker") == HttpStatusCode.NotFound, "The setup node sheet closes once an administrator exists.");
        using (var savedAi = await admin.SendAsync(HttpMethod.Put, "/api/v1/tools/ai/settings", new Encoding.Contracts.Tools.AiSettingsUpdateRequest(OpenAiApiKey: "private-ai-http-test-key")))
        {
            Check(savedAi.IsSuccessStatusCode, "An administrator can configure AI on a fresh standalone server.");
            var text = await savedAi.Content.ReadAsStringAsync();
            Check(!text.Contains("private-ai-http-test-key") && text.Contains("\"hasOpenAiApiKey\":true"), "Saving AI settings never echoes the secret.");
        }
        using (var readAi = await admin.SendAsync(HttpMethod.Get, "/api/v1/tools/ai/settings"))
            Check(!(await readAi.Content.ReadAsStringAsync()).Contains("private-ai-http-test-key"), "Reading AI settings never returns the secret.");
        Check(await admin.StatusAsync(post, "/api/v1/tools/ai/codex-cli/auth/complete") is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            "There is no endpoint to simulate AI authorization.");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 1).Select(_ => anonymous.StatusAsync(post, "/api/v1/auth/setup", new SetupRequest("racer", AdminPassword), csrf: false)));
        Check(concurrent.All(code => code == HttpStatusCode.Conflict), "A later setup attempt never creates a second administrator.");

        Check(await admin.StatusAsync(get, "/api/v1/tools/ai/quotas") == HttpStatusCode.OK, "An administrator reads subscription quota observations.");
        Check(await admin.StatusAsync(get, "/api/v1/tools/ai/sessions") == HttpStatusCode.OK, "An administrator reads AI session summaries.");
        // Administrateur : lecture, écriture avec protection CSRF.
        Check(await admin.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.OK, "An administrator reads.");
        Check(await admin.StatusAsync(post, "/api/v1/libraries", new { name = "sans-csrf" }, csrf: false) == HttpStatusCode.Forbidden, "Cookie-authenticated writes need the anti-CSRF header.");
        Check(await admin.StatusAsync(post, "/api/v1/libraries", new { name = "films" }) == HttpStatusCode.Created, "An administrator writes.");
        Check(await admin.StatusAsync(get, "/api/v1/users") == HttpStatusCode.OK, "Administrators list users.");

        // Rôle lecture.
        Check(await admin.StatusAsync(post, "/api/v1/users", new CreateUserRequest("lecteur", "lecteur-mot-de-passe", UserRole.Viewer)) == HttpStatusCode.Created, "Administrators create users.");
        Check(await admin.StatusAsync(post, "/api/v1/users", new CreateUserRequest("LECTEUR", "lecteur-mot-de-passe", UserRole.Viewer)) == HttpStatusCode.Conflict, "User names are unique regardless of case.");
        Check(await viewer.LoginAsync("Lecteur", "lecteur-mot-de-passe") == HttpStatusCode.OK, "Login ignores the case of the user name.");
        using (var version = await viewer.SendAsync(get, "/api/v1/system/version"))
        {
            Check(version.StatusCode == HttpStatusCode.OK, "A viewer can read the installed version.");
            var body = await version.Content.ReadFromJsonAsync<JsonElement>();
            Check(body.GetProperty("version").GetString() == "0.0.0-dev", "Local builds expose their actual development version.");
        }
        Check(await viewer.StatusAsync(get, "/api/v1/system/updates") == HttpStatusCode.OK, "A viewer can read release notes.");
        Check(await viewer.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.OK, "A viewer reads.");
        Check(await viewer.StatusAsync(post, "/api/v1/libraries", new { name = "interdit" }) == HttpStatusCode.Forbidden, "A viewer cannot create.");
        Check(await viewer.StatusAsync(delete, "/api/v1/libraries/" + Guid.NewGuid()) == HttpStatusCode.Forbidden, "A viewer cannot delete.");
        Check(await viewer.StatusAsync(get, "/api/v1/tools/ai/quotas") == HttpStatusCode.Forbidden, "A viewer cannot read account subscription quotas.");
        Check(await viewer.StatusAsync(get, "/api/v1/tools/ai/sessions") == HttpStatusCode.Forbidden, "A viewer cannot list private AI sessions.");
        Check(await viewer.StatusAsync(get, "/api/v1/users") == HttpStatusCode.Forbidden, "A viewer cannot list users.");
        // Le central existe dès l'installation : il a un nom, qu'un administrateur peut changer ; il n'y a plus de création.
        using (var identity = await anonymous.SendAsync(get, "/api/v1/central"))
            Check((await identity.Content.ReadFromJsonAsync<JsonElement>(Json)).TryGetProperty("name", out _), "The central always has a name.");
        Check(await viewer.StatusAsync(put, "/api/v1/central", new { name = "Maison" }) == HttpStatusCode.Forbidden, "A viewer cannot rename the central.");
        Check(await admin.StatusAsync(put, "/api/v1/central", new { name = "  " }) == HttpStatusCode.BadRequest, "The central needs a name.");
        Check(await admin.StatusAsync(put, "/api/v1/central", new { name = "Atelier" }) == HttpStatusCode.OK, "The central can be renamed.");
        var creation = await admin.StatusAsync(post, "/api/v1/central", new { name = "Autre" });
        Check(creation is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, "There is no route to create a central: " + creation);
        // Le central ne rejoint jamais un autre central : plus de route d'adoption d'identité.
        var adoption = await admin.StatusAsync(post, "/api/v1/central/join", new { centralId = Guid.NewGuid() });
        Check(adoption is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, "The cluster-join route no longer exists: " + adoption);
        Check(await viewer.StatusAsync(post, "/api/v1/local-worker/leave") == HttpStatusCode.Forbidden, "A viewer cannot make this node leave its cluster.");
        Check(await viewer.StatusAsync(post, "/api/v1/local-worker/enroll", new { centralId = Guid.NewGuid() }) == HttpStatusCode.Forbidden, "A viewer cannot enroll this node elsewhere.");
        Check(await admin.StatusAsync(post, "/api/v1/local-worker/leave") == HttpStatusCode.ServiceUnavailable, "Without a local worker function, the relay answers with a clear refusal.");
        var masked = JsonDocument.Parse(Encoding.Api.Workers.LocalWorkerEndpoints.HideSecrets("{\"name\":\"n\",\"pairingKey\":\"K\",\"verificationCode\":\"C\"}")).RootElement;
        Check(masked.GetProperty("name").GetString() == "n" && masked.GetProperty("pairingKey").ValueKind == JsonValueKind.Null && masked.GetProperty("verificationCode").ValueKind == JsonValueKind.Null,
            "A viewer never sees the pairing key or the verification code.");
        Check(await viewer.StatusAsync(post, "/api/v1/workers/enrollment-requests/approve", new ApproveWorkerEnrollmentRequest("0123456789ABCDEF")) == HttpStatusCode.Forbidden, "A viewer cannot approve a node.");
        Check(await viewer.StatusAsync(put, "/api/v1/workers/enrollment-window", new OpenEnrollmentWindowRequest(5)) == HttpStatusCode.Forbidden, "A viewer cannot open enrollment.");
        Check(await viewer.StatusAsync(post, "/api/v1/plugins/base/connection-test", new { }) == HttpStatusCode.Forbidden, "A viewer cannot make the server call external services.");
        var validation = await viewer.StatusAsync(post, "/api/v1/flows/validate", JsonDocument.Parse("{}").RootElement);
        Check(validation is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest or HttpStatusCode.OK, "Read-only POST operations stay available to viewers: " + validation);
        Check(await viewer.StatusAsync(put, "/api/v1/auth/password", new ChangePasswordRequest("mauvais-actuel", "nouveau-mot-de-passe")) == HttpStatusCode.BadRequest, "Changing a password requires the current one.");
        Check(await viewer.StatusAsync(put, "/api/v1/auth/password", new ChangePasswordRequest("lecteur-mot-de-passe", "nouveau-mot-de-passe")) == HttpStatusCode.NoContent, "A viewer changes their own password.");
        Check(await new Client(factory.CreateClient()).LoginAsync("lecteur", "lecteur-mot-de-passe") == HttpStatusCode.Unauthorized, "The old password stops working.");
        Check(await new Client(factory.CreateClient()).LoginAsync("lecteur", "nouveau-mot-de-passe") == HttpStatusCode.OK, "The new password logs in.");
        Check(await admin.StatusAsync(put, "/api/v1/auth/password", new ChangePasswordRequest(AdminPassword, "nouveau-admin-mot-de-passe")) == HttpStatusCode.NoContent, "An administrator changes their own password.");
        Check(await new Client(factory.CreateClient()).LoginAsync("root", "nouveau-admin-mot-de-passe") == HttpStatusCode.OK, "The administrator's new password logs in.");
        Check(await admin.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.OK, "The administrator's current session survives the change.");
        Check(await admin.StatusAsync(put, "/api/v1/auth/password", new ChangePasswordRequest("nouveau-admin-mot-de-passe", AdminPassword)) == HttpStatusCode.NoContent, "The administrator restores the original password.");

        // Verrouillage progressif, sans énumération des comptes.
        Check(await admin.StatusAsync(post, "/api/v1/users", new CreateUserRequest("cible", "cible-mot-de-passe-1", UserRole.Viewer)) == HttpStatusCode.Created, "Create a throttling target.");
        var target = new Client(factory.CreateClient());
        for (var attempt = 0; attempt < 5; attempt++)
            Check(await target.LoginAsync("cible", "mauvais mot de passe") == HttpStatusCode.Unauthorized, "A wrong password is refused.");
        Check(await target.LoginAsync("cible", "cible-mot-de-passe-1") == HttpStatusCode.Unauthorized, "Repeated failures lock the account even for the right password.");
        Check(await target.LoginAsync("inconnu", "mauvais mot de passe") == HttpStatusCode.Unauthorized, "Unknown accounts fail identically.");
        // Un administrateur peut débloquer en réinitialisant le mot de passe.
        var users = (await (await admin.SendAsync(get, "/api/v1/users")).Content.ReadFromJsonAsync<List<UserResponse>>(Json))!;
        var viewerUser = users.Single(user => user.Username == "lecteur");
        var targetUser = users.Single(user => user.Username == "cible");
        Check(await admin.StatusAsync(put, $"/api/v1/users/{targetUser.Id}/password", new ResetPasswordRequest("cible-mot-de-passe-2")) == HttpStatusCode.NoContent, "An administrator resets a password.");
        Check(await target.LoginAsync("cible", "cible-mot-de-passe-2") == HttpStatusCode.OK, "A reset clears the lock.");

        // Révocation des sessions.
        Check(await admin.StatusAsync(put, $"/api/v1/users/{targetUser.Id}", new UpdateUserRequest(UserRole.Viewer, false)) == HttpStatusCode.NoContent, "Deactivate a user.");
        Check(await target.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.Unauthorized, "Deactivation revokes live sessions.");
        Check(await new Client(factory.CreateClient()).LoginAsync("cible", "cible-mot-de-passe-2") == HttpStatusCode.Unauthorized, "A deactivated user cannot log in.");
        Check(await viewer.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.OK, "The session of a user who changed password remains valid.");
        Check(await admin.StatusAsync(put, $"/api/v1/users/{viewerUser.Id}", new UpdateUserRequest(UserRole.Admin, true)) == HttpStatusCode.NoContent, "Promote a viewer.");
        Check(await viewer.StatusAsync(post, "/api/v1/libraries", new { name = "promu" }) == HttpStatusCode.Created, "A role change applies to live sessions immediately.");
        Check(await viewer.StatusAsync(post, "/api/v1/auth/logout", csrf: true) == HttpStatusCode.NoContent, "Logout succeeds.");
        Check(await viewer.StatusAsync(get, "/api/v1/libraries") == HttpStatusCode.Unauthorized, "A logged out session is dead server-side.");

        // Garde du dernier administrateur.
        var rootId = users.Single(user => user.Username == "root").Id;
        Check(await admin.StatusAsync(put, $"/api/v1/users/{rootId}", new UpdateUserRequest(UserRole.Viewer, true)) == HttpStatusCode.Conflict, "You cannot demote yourself.");
        Check(await admin.StatusAsync(delete, $"/api/v1/users/{rootId}") == HttpStatusCode.Conflict, "You cannot delete yourself.");
        var repository = factory.Services.GetRequiredService<UserRepository>();
        Check(await repository.UpdateAsync(viewerUser.Id, UserRole.Viewer, true, CancellationToken.None) == UserChangeResult.Updated, "Demote the second administrator.");
        Check(await repository.DeleteAsync(rootId, CancellationToken.None) == UserChangeResult.LastAdministrator, "The last administrator cannot be removed.");
        Check(await repository.UpdateAsync(rootId, UserRole.Admin, false, CancellationToken.None) == UserChangeResult.LastAdministrator, "The last administrator cannot be deactivated.");
        Console.WriteLine("PASS: setup, roles, CSRF header, lockout, session revocation and last-administrator guard over HTTP.");

        await PairingAsync(factory, anonymous, admin, viewer);
    }

    private static async Task PairingAsync(WebApplicationFactory<ApiAssembly> factory, Client anonymous, Client admin, Client viewer)
    {
        var get = HttpMethod.Get; var post = HttpMethod.Post; var put = HttpMethod.Put; var delete = HttpMethod.Delete;
        Check(await viewer.LoginAsync("lecteur", "nouveau-mot-de-passe") == HttpStatusCode.OK, "Viewer logs in again.");

        // Enrôlement direct : fermé par défaut.
        var first = NewWorker(Guid.NewGuid(), NodeToken());
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", first, csrf: false) == HttpStatusCode.Forbidden, "Direct enrollment of a new identity is closed by default.");
        Check(await anonymous.StatusAsync(put, "/api/v1/workers/enrollment-window", new OpenEnrollmentWindowRequest(5), csrf: false) == HttpStatusCode.Unauthorized, "Anonymous callers cannot open the window.");
        Check(await admin.StatusAsync(put, "/api/v1/workers/enrollment-window", new OpenEnrollmentWindowRequest(0)) == HttpStatusCode.BadRequest, "The window duration is bounded.");
        Check(await admin.StatusAsync(put, "/api/v1/workers/enrollment-window", new OpenEnrollmentWindowRequest(5)) == HttpStatusCode.OK, "An administrator opens the window.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", first, csrf: false) == HttpStatusCode.OK, "A new identity enrolls while the window is open.");
        var otherVersion = NewWorker(Guid.NewGuid(), NodeToken()) with { Version = "0.0.0-autre" };
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", otherVersion, csrf: false) == HttpStatusCode.BadRequest, "A worker with another version than the central cannot enroll.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", otherVersion with { Version = null }, csrf: false) == HttpStatusCode.BadRequest, "A worker that declares no version cannot enroll.");
        var stolen = first with { WorkerToken = NodeToken() };
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", stolen, csrf: false) == HttpStatusCode.Conflict, "Another secret cannot take over an enrolled identity, even with the window open.");
        Check(await admin.StatusAsync(delete, "/api/v1/workers/enrollment-window") == HttpStatusCode.NoContent, "An administrator closes the window.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", first, csrf: false) == HttpStatusCode.OK, "The same identity can repeat its enrollment after the window closes.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", stolen, csrf: false) == HttpStatusCode.Forbidden, "A different secret is refused once the window is closed.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", NewWorker(Guid.NewGuid(), NodeToken()), csrf: false) == HttpStatusCode.Forbidden, "Another new identity is refused once the window is closed.");
        // ADR 0034 : le compte administrateur du central admet un worker hors fenêtre ; ni un lecteur, ni une requête sans en-tête anti-CSRF.
        var byAdministrator = NewWorker(Guid.NewGuid(), NodeToken());
        Check(await viewer.StatusAsync(post, "/api/v1/workers/enroll", byAdministrator) == HttpStatusCode.Forbidden, "A viewer session cannot admit a node.");
        Check(await admin.StatusAsync(post, "/api/v1/workers/enroll", byAdministrator, csrf: false) == HttpStatusCode.Forbidden, "An administrator session needs the anti-CSRF header to admit a node.");
        Check(await admin.StatusAsync(post, "/api/v1/workers/enroll", byAdministrator) == HttpStatusCode.OK, "An administrator session admits a node outside the enrollment window.");
        Check(await admin.StatusAsync(post, "/api/v1/workers/enroll", byAdministrator with { WorkerToken = NodeToken() }) == HttpStatusCode.Conflict, "Even an administrator cannot take over an enrolled identity with another secret.");
        var window = (await (await admin.SendAsync(get, "/api/v1/workers/enrollment-window")).Content.ReadFromJsonAsync<EnrollmentWindowResponse>(Json))!;
        Check(!window.IsOpen, "The window reports closed.");

        // Worker local de l'image unique : enrôlé d'office, sans fenêtre, par le secret du conteneur ; nom choisi à l'installation.
        var localWorker = NewWorker(Guid.NewGuid(), NodeToken());
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", localWorker, csrf: false, localSecret: "mauvais-secret") == HttpStatusCode.Forbidden, "A wrong local secret does not bypass the closed window.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", localWorker, csrf: false, localSecret: "") == HttpStatusCode.Forbidden, "An empty local secret does not bypass the closed window.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", localWorker, csrf: false, localSecret: LocalSecret) == HttpStatusCode.OK, "The local node enrolls by itself with the container secret while the window is closed.");
        var listed = (await (await admin.SendAsync(get, "/api/v1/workers")).Content.ReadFromJsonAsync<List<WorkerResponse>>(Json))!;
        Check(listed.Single(worker => worker.Id == localWorker.Id).DisplayName == "Serveur du salon", "The friendly name chosen at setup is applied to the local node.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", NewWorker(Guid.NewGuid(), NodeToken()), csrf: false) == HttpStatusCode.Forbidden, "Other nodes still need the window or a pairing key.");

        // Un worker retiré apprend que son identité est révoquée (410) au lieu de rester « enrôlé » sans jamais être vu ; il ne revient que sous un nouvel identifiant (0017).
        var localBeat = new WorkerHeartbeatRequest(localWorker.Name, localWorker.Roles, 1, localWorker.Capabilities, Version: AppVersion.Current);
        Check(await anonymous.StatusAsync(post, $"/api/v1/workers/{localWorker.Id}/leave", csrf: false, nodeToken: localWorker.WorkerToken) == HttpStatusCode.NoContent, "A node leaves with its own token.");
        Check(await anonymous.StatusAsync(put, $"/api/v1/workers/{localWorker.Id}/heartbeat", localBeat, csrf: false, nodeToken: localWorker.WorkerToken) == HttpStatusCode.Gone, "A removed node is told its identity was revoked.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", localWorker, csrf: false, localSecret: LocalSecret) == HttpStatusCode.Conflict, "A removed identity is never re-enrolled, even by the container secret.");
        var reborn = NewWorker(Guid.NewGuid(), NodeToken());
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enroll", reborn, csrf: false, localSecret: LocalSecret) == HttpStatusCode.OK, "The same node re-enrolls under a fresh identity.");
        Check(await anonymous.StatusAsync(put, $"/api/v1/workers/{Guid.NewGuid()}/heartbeat", localBeat, csrf: false, nodeToken: NodeToken()) == HttpStatusCode.Unauthorized, "An unknown identity is still a plain 401.");

        // Les workers gardent leur propre authentification, sans session.
        var token = first.WorkerToken;
        var heartbeat = new WorkerHeartbeatRequest(first.Name, first.Roles, 1, first.Capabilities, Version: AppVersion.Current);
        Check(await anonymous.StatusAsync(put, $"/api/v1/workers/{first.Id}/heartbeat", heartbeat, csrf: false, nodeToken: token) == HttpStatusCode.OK, "A worker heartbeats with its token only.");
        Check(await anonymous.StatusAsync(put, $"/api/v1/workers/{first.Id}/heartbeat", heartbeat, csrf: false, nodeToken: NodeToken()) == HttpStatusCode.Unauthorized, "A heartbeat with a wrong token is refused.");
        Check(await anonymous.StatusAsync(get, $"/api/v1/runtime/workers/{first.Id}/capacity", nodeToken: token) == HttpStatusCode.OK, "Runtime routes authenticate by node token.");
        Check(await anonymous.StatusAsync(get, $"/api/v1/runtime/workers/{first.Id}/capacity") == HttpStatusCode.Unauthorized, "Runtime routes refuse requests without a token.");
        var execution = $"/api/v1/executions/{Guid.NewGuid()}";
        Check(await anonymous.StatusAsync(get, execution, nodeToken: token) == HttpStatusCode.NotFound, "A worker may read the state of an execution (authorized, then not found).");
        Check(await anonymous.StatusAsync(get, execution, nodeToken: NodeToken()) == HttpStatusCode.Unauthorized, "An unknown node token cannot read executions.");
        Check(await anonymous.StatusAsync(get, "/api/v1/executions", nodeToken: token) == HttpStatusCode.Unauthorized, "A node token opens no other management route.");
        Check(await anonymous.StatusAsync(post, $"/api/v1/executions/{Guid.NewGuid()}/cancel", csrf: false, nodeToken: token) == HttpStatusCode.Unauthorized, "A node token cannot modify management state.");

        // Jumelage par clé : demandes publiques bornées, approbation réservée aux administrateurs.
        var pairing = new[] { "0123456789ABCDEF", "FEDCBA9876543210", "AAAAAAAAAAAAAAAA" };
        var requests = pairing.Select(key => new CreateWorkerEnrollmentRequest(NewWorker(Guid.NewGuid(), NodeToken()), key)).ToArray();
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enrollment-requests", requests[0], csrf: false) == HttpStatusCode.Accepted, "A worker can publish a pairing request.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enrollment-requests", requests[1], csrf: false) == HttpStatusCode.Accepted, "A second request is accepted.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enrollment-requests", requests[2], csrf: false) == HttpStatusCode.TooManyRequests, "Pending pairing requests are capped.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enrollment-requests", requests[0], csrf: false) == HttpStatusCode.Accepted, "A pending worker can refresh its own request at the cap.");
        Check(await anonymous.StatusAsync(get, "/api/v1/workers/enrollment-requests") == HttpStatusCode.Unauthorized, "Pending requests are not public.");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enrollment-requests/approve", new ApproveWorkerEnrollmentRequest(pairing[0]), csrf: false) == HttpStatusCode.Unauthorized, "Approval by key needs a session.");
        Check(await anonymous.StatusAsync(post, $"/api/v1/workers/enrollment-requests/{requests[0].Worker.Id}/approve", csrf: false) == HttpStatusCode.Unauthorized, "Approval by identifier needs a session.");
        Check(await viewer.StatusAsync(post, $"/api/v1/workers/enrollment-requests/{requests[0].Worker.Id}/approve") == HttpStatusCode.Forbidden, "Viewers cannot approve.");
        Check(await viewer.StatusAsync(get, "/api/v1/workers/enrollment-requests") == HttpStatusCode.OK, "Viewers can see pending requests.");
        Check(await admin.StatusAsync(post, "/api/v1/workers/enrollment-requests/approve", new ApproveWorkerEnrollmentRequest(pairing[0])) == HttpStatusCode.OK, "An administrator approves by key.");
        Check(await admin.StatusAsync(post, $"/api/v1/workers/enrollment-requests/{requests[1].Worker.Id}/approve") == HttpStatusCode.OK, "An administrator approves by identifier.");
        Check(await anonymous.StatusAsync(put, $"/api/v1/workers/{requests[0].Worker.Id}/heartbeat", new WorkerHeartbeatRequest(requests[0].Worker.Name, requests[0].Worker.Roles, 1, requests[0].Worker.Capabilities), csrf: false, nodeToken: requests[0].Worker.WorkerToken) == HttpStatusCode.OK,
            "A paired worker heartbeats with the secret it generated.");

        // Le mode d'un worker ne se change plus à distance : le central est fixe, un worker reste worker.
        var removed = new[] { await admin.StatusAsync(put, $"/api/v1/workers/{first.Id}/mode", new { server = true, worker = true }), await admin.StatusAsync(post, "/api/v1/central/leadership/transfer", new { targetWorkerId = first.Id }) };
        Check(removed.All(status => status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed), "The remote node-mode route no longer exists: " + string.Join(",", removed));
        Check(await anonymous.StatusAsync(get, "/api/v1/central/leadership/backup/latest", csrf: false, nodeToken: token) == HttpStatusCode.NotFound, "A node token can no longer download the database backup.");
        Check(await anonymous.StatusAsync(get, "/api/v1/central/leadership/keys", csrf: false, nodeToken: token) == HttpStatusCode.NotFound, "A node token can no longer download data protection keys.");

        // Départ volontaire d'un central (0039) : authentifié par le jeton du worker lui-même, jamais par celui d'un autre.
        var leaving = requests[1].Worker;
        var leave = $"/api/v1/workers/{leaving.Id}/leave";
        Check(await anonymous.StatusAsync(post, leave, csrf: false) == HttpStatusCode.Unauthorized, "Leaving a cluster needs the node token.");
        Check(await anonymous.StatusAsync(post, leave, csrf: false, nodeToken: token) == HttpStatusCode.Unauthorized, "Another node's token cannot make a node leave.");
        Check(await anonymous.StatusAsync(post, leave, csrf: false, nodeToken: leaving.WorkerToken) == HttpStatusCode.NoContent, "A node leaves its cluster with its own token.");
        Check(await anonymous.StatusAsync(post, leave, csrf: false, nodeToken: leaving.WorkerToken) == HttpStatusCode.Unauthorized, "A node that left cannot act again with its old identity.");
        Check(await anonymous.StatusAsync(put, $"/api/v1/workers/{leaving.Id}/heartbeat", new WorkerHeartbeatRequest(leaving.Name, leaving.Roles, 1, leaving.Capabilities), csrf: false, nodeToken: leaving.WorkerToken) == HttpStatusCode.Gone, "A node that left is not reactivated by a heartbeat (it is told its identity is revoked).");
        var after = (await (await admin.SendAsync(get, "/api/v1/workers")).Content.ReadFromJsonAsync<List<WorkerResponse>>(Json))!;
        Check(after.All(worker => worker.Id != leaving.Id), "A node that left no longer appears in the cluster.");

        // Cycle de vie des workers et compatibilité (0041).
        WorkerHeartbeatRequest Signal(string? version, string? nonce) => new(first.Name, first.Roles, 1, first.Capabilities, null, version, nonce);
        async Task<WorkerResponse?> SendBeat(WorkerHeartbeatRequest request) =>
            await (await anonymous.SendAsync(put, $"/api/v1/workers/{first.Id}/heartbeat", request, csrf: false, nodeToken: token)).Content.ReadFromJsonAsync<WorkerResponse>(Json);
        async Task<WorkerResponse> Listed() => (await (await admin.SendAsync(get, "/api/v1/workers")).Content.ReadFromJsonAsync<List<WorkerResponse>>(Json))!.Single(worker => worker.Id == first.Id);
        Check((await SendBeat(Signal(AppVersion.Current, "aaaa")))?.Lifecycle == WorkerLifecycle.Online, "A compatible node is online.");
        Check((await SendBeat(Signal(AppVersion.Current + "-autre", "aaaa")))?.Lifecycle == WorkerLifecycle.Incompatible && (await Listed()).LifecycleReason!.Contains("mettez le worker à jour"), "A worker whose version differs from the central is incompatible and told to update.");
        Check((await SendBeat(Signal(AppVersion.Current, "aaaa")))?.Lifecycle == WorkerLifecycle.Online, "The node is compatible again as soon as it has the central version.");
        Check(await viewer.StatusAsync(put, $"/api/v1/workers/{first.Id}/draining", new DrainWorkerRequest(true)) == HttpStatusCode.Forbidden, "Viewers cannot drain a node.");
        Check(await admin.StatusAsync(put, $"/api/v1/workers/{first.Id}/draining", new DrainWorkerRequest(true)) == HttpStatusCode.Accepted && (await Listed()).Lifecycle == WorkerLifecycle.Draining, "An administrator drains a node.");
        Check(await admin.StatusAsync(put, $"/api/v1/workers/{first.Id}/draining", new DrainWorkerRequest(false)) == HttpStatusCode.Accepted && (await Listed()).Lifecycle == WorkerLifecycle.Online, "Resuming a drained node puts it back online.");
        Check(await admin.StatusAsync(put, $"/api/v1/workers/{Guid.NewGuid()}/draining", new DrainWorkerRequest(true)) == HttpStatusCode.NotFound, "Draining an unknown node is reported.");
        // Un redémarrage change le nonce une fois ; deux processus qui se relaient (a, b, a) partagent la même identité.
        Check((await SendBeat(Signal(AppVersion.Current, "bbbb")))?.Lifecycle == WorkerLifecycle.Online, "A restart (new session nonce) is not an incident.");
        Check((await SendBeat(Signal(AppVersion.Current, "cccc")))?.Lifecycle == WorkerLifecycle.Online, "Another restart is not an incident either.");
        Check((await SendBeat(Signal(AppVersion.Current, "bbbb")))?.Lifecycle == WorkerLifecycle.Quarantined, "Two processes alternating on one identity put the node in quarantine.");
        Check((await Listed()).Lifecycle == WorkerLifecycle.Quarantined, "The quarantine is visible in the node list.");
        Check(await viewer.StatusAsync(post, $"/api/v1/workers/{first.Id}/quarantine/clear") == HttpStatusCode.Forbidden, "Viewers cannot lift a quarantine.");
        Check(await admin.StatusAsync(post, $"/api/v1/workers/{first.Id}/quarantine/clear") == HttpStatusCode.Accepted, "An administrator lifts the quarantine.");
        Check(await admin.StatusAsync(post, $"/api/v1/workers/{first.Id}/quarantine/clear") == HttpStatusCode.NotFound, "Lifting a quarantine twice is reported.");
        Check((await SendBeat(Signal(AppVersion.Current, "dddd")))?.Lifecycle == WorkerLifecycle.Online, "After the quarantine the node works again.");

        // Première liaison implicite (0040) : réservée aux administrateurs, à usage unique, désactivée par défaut.
        Check(!(await (await admin.SendAsync(get, "/api/v1/workers/first-link")).Content.ReadFromJsonAsync<FirstLinkResponse>(Json))!.IsActive, "First-link acceptance is off by default.");
        Check(await anonymous.StatusAsync(put, "/api/v1/workers/first-link", new OpenEnrollmentWindowRequest(5), csrf: false) == HttpStatusCode.Unauthorized, "Anonymous callers cannot enable first-link acceptance.");
        Check(await viewer.StatusAsync(put, "/api/v1/workers/first-link", new OpenEnrollmentWindowRequest(5)) == HttpStatusCode.Forbidden, "Viewers cannot enable first-link acceptance.");
        Check(await admin.StatusAsync(put, "/api/v1/workers/first-link", new OpenEnrollmentWindowRequest(0)) == HttpStatusCode.BadRequest, "The first-link duration is bounded.");
        var pendingBefore = new CreateWorkerEnrollmentRequest(NewWorker(Guid.NewGuid(), NodeToken()), "BBBBBBBBBBBBBBBB");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enrollment-requests", pendingBefore, csrf: false) == HttpStatusCode.Accepted, "A request made while the mode is off stays pending.");
        Check(await anonymous.StatusAsync(put, $"/api/v1/workers/{pendingBefore.Worker.Id}/heartbeat", new WorkerHeartbeatRequest(pendingBefore.Worker.Name, pendingBefore.Worker.Roles, 1, pendingBefore.Worker.Capabilities), csrf: false, nodeToken: pendingBefore.Worker.WorkerToken) == HttpStatusCode.Unauthorized, "A pending node is not accepted without approval.");
        var pendingList = (await (await admin.SendAsync(get, "/api/v1/workers/enrollment-requests")).Content.ReadFromJsonAsync<List<PendingWorkerEnrollmentResponse>>(Json))!;
        Check(pendingList.Single(item => item.Id == pendingBefore.Worker.Id).VerificationCode == Encoding.Core.Workers.WorkerVerificationCode.FromToken(pendingBefore.Worker.Id, pendingBefore.Worker.WorkerToken),
            "The pending list shows the verification code the node computes on its side.");
        Check(await admin.StatusAsync(put, "/api/v1/workers/first-link", new OpenEnrollmentWindowRequest(5)) == HttpStatusCode.OK, "An administrator enables first-link acceptance.");
        var firstLink = new CreateWorkerEnrollmentRequest(NewWorker(Guid.NewGuid(), NodeToken()), "CCCCCCCCCCCCCCCC");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enrollment-requests", firstLink, csrf: false) == HttpStatusCode.Accepted, "The first request is received.");
        Check(await anonymous.StatusAsync(put, $"/api/v1/workers/{firstLink.Worker.Id}/heartbeat", new WorkerHeartbeatRequest(firstLink.Worker.Name, firstLink.Worker.Roles, 1, firstLink.Worker.Capabilities), csrf: false, nodeToken: firstLink.Worker.WorkerToken) == HttpStatusCode.OK,
            "The first request is accepted without verification.");
        Check(!(await (await admin.SendAsync(get, "/api/v1/workers/first-link")).Content.ReadFromJsonAsync<FirstLinkResponse>(Json))!.IsActive, "First-link acceptance switches itself off after one link.");
        var second = new CreateWorkerEnrollmentRequest(NewWorker(Guid.NewGuid(), NodeToken()), "DDDDDDDDDDDDDDDD");
        Check(await anonymous.StatusAsync(post, "/api/v1/workers/enrollment-requests", second, csrf: false) == HttpStatusCode.Accepted, "The next request is received.");
        Check(await anonymous.StatusAsync(put, $"/api/v1/workers/{second.Worker.Id}/heartbeat", new WorkerHeartbeatRequest(second.Worker.Name, second.Worker.Roles, 1, second.Worker.Capabilities, Version: AppVersion.Current), csrf: false, nodeToken: second.Worker.WorkerToken) == HttpStatusCode.Unauthorized,
            "The next request needs approval again.");

        // Export des bibliothèques avant de quitter un central : lecture seule, session requise.
        Check(await anonymous.StatusAsync(get, "/api/v1/libraries/export") == HttpStatusCode.Unauthorized, "Library export needs a session.");
        Check(await viewer.StatusAsync(get, "/api/v1/libraries/export") == HttpStatusCode.OK, "Viewers can export library definitions.");
        Console.WriteLine("PASS: enrollment window, capped pairing requests, admin-only approval and node-token isolation.");
    }

    private static Task EndpointCatalogueAsync(WebApplicationFactory<ApiAssembly> factory)
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        string Describe(RouteEndpoint endpoint) =>
            (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "*") + " " + endpoint.RoutePattern.RawText;
        var anonymous = endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null).Select(Describe).Order().ToArray();
        var expected = new[]
        {
            "GET {*path:nonfile}",
            "GET /api/v1/auth/status",
            "GET /api/v1/auth/setup/worker",
            "GET /api/v1/central",
            "GET /health/live",
            "* /health/ready",
            "POST /api/v1/auth/login",
            "POST /api/v1/auth/setup",
            "POST /api/v1/integrations/{pluginId}/webhook",
            "POST /api/v1/workers/enroll",
            "POST /api/v1/workers/enrollment-requests",
            "POST /api/v1/workers/{workerId:guid}/leave",
            "PUT /api/v1/workers/{workerId:guid}/heartbeat",
        };
        var runtime = anonymous.Where(item => item.Contains("/api/v1/runtime/workers/{workerId:guid}")).ToArray();
        var rest = anonymous.Except(runtime).ToArray();
        Check(runtime.Length > 10, "The runtime group is self-authenticated by node token.");
        Check(runtime.Contains("POST /api/v1/runtime/workers/{workerId:guid}/executions/{id:guid}/source/prepare"),
            "Deferred source preparation belongs to the group authenticated by node credentials and fenced by lease epoch.");
        Check(runtime.Contains("POST /api/v1/runtime/workers/{workerId:guid}/executions/{id:guid}/source-write/authorize"),
            "Source-write coordination uses the node-token authenticated runtime group and fenced claims.");
        Check(rest.SequenceEqual(expected.Order()), "Only these routes may skip user authentication; update the list deliberately:\n" + string.Join("\n", rest));
        Console.WriteLine("PASS: the anonymous route catalogue matches the reviewed list.");
        return Task.CompletedTask;
    }
}
