using Encoding.Core.Nodes;

namespace Encoding.Api.Nodes;

/// <summary>Authentifie chaque appel au même conteneur, sans transmettre de secret à une adresse distante.</summary>
public sealed class LocalAgentAuthenticationHandler(IConfiguration configuration) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !LocalEnrollment.IsLoopback(request.RequestUri.AbsoluteUri))
            throw new HttpRequestException("L'agent local doit être joignable en boucle locale.");
        var secret = LocalEnrollment.Read(configuration[LocalEnrollment.SecretFileSetting]);
        if (string.IsNullOrEmpty(secret))
            throw new HttpRequestException("Le secret de communication avec l'agent local est indisponible.");

        request.Headers.Remove(LocalEnrollment.HeaderName);
        request.Headers.Add(LocalEnrollment.HeaderName, secret);
        return base.SendAsync(request, cancellationToken);
    }
}
