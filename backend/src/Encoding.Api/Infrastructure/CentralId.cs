namespace Encoding.Api.Infrastructure;

/// <summary>Identifiant stable du central (lu une fois dans <c>cluster_identity</c>), utilisé pour préfixer les sessions IA.</summary>
public sealed class CentralId(CentralIdentityRepository repository)
{
    private Guid value;

    public async ValueTask<Guid> GetAsync(CancellationToken cancellationToken)
    {
        if (value != Guid.Empty) return value;
        value = (await repository.GetAsync(cancellationToken)).Id;
        return value;
    }
}
