using Encoding.Api.Executions;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>#26 (banc NAS #18) : la perte de bail d'un worker a un code d'échec dédié et stable, pas le générique <c>execution_failed</c>.</summary>
public sealed class ExecutionFailureCodeTests
{
    [Theory]
    // Les trois détails écrits par RuntimeRecoveryService à l'expiration du bail.
    [InlineData("worker_perdu : le worker a perdu son bail pendant cette tentative. Elle est en erreur, sans réattribution ; une relance explicite doit créer une nouvelle exécution.")]
    [InlineData("worker_perdu : le worker a perdu son bail pendant une étape à effet externe. Le résultat est incertain ; la tentative est en erreur et une relance explicite doit créer une nouvelle exécution.")]
    [InlineData("worker_perdu : reprise automatique héritée clôturée après mise à niveau, sans réattribution.")]
    public void LeaseLossMapsToWorkerLost(string detail) =>
        Assert.Equal("worker_lost", ExecutionRepository.FailureCode(detail));

    [Theory]
    [InlineData("media_tool_failed : bloc execute. L’outil ffmpeg a échoué (code 234).", "media_tool_failed")]
    [InlineData("execution_infrastructure_failed : bloc validate.", "execution_infrastructure_failed")]
    [InlineData("placement_impossible : aucun worker éligible.", "placement_impossible")]
    [InlineData("Une erreur sans préfixe connu.", "execution_failed")]
    [InlineData("worker_perdu_autre : préfixe voisin", "execution_failed")]
    [InlineData("", "execution_failed")]
    public void OtherCausesKeepTheirCode(string detail, string expected) =>
        Assert.Equal(expected, ExecutionRepository.FailureCode(detail));
}
