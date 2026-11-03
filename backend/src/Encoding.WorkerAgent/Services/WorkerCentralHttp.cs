namespace Encoding.WorkerAgent.Services;

/// <summary>Les requêtes authentifiées restent sur le central choisi : une redirection HTTP n'emporte jamais le jeton.</summary>
internal static class WorkerCentralHttp
{
    public static HttpClient Create(TimeSpan timeout) =>
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout };
}
