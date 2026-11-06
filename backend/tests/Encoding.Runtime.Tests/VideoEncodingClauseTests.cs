using Encoding.Api.Executions;
using Encoding.Contracts.Executions;
using Encoding.Contracts.Flows;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>Diagnostic de video_encoding_invalid : chaque clause qui échoue est nommée, avec attendu / reçu pour les champs.</summary>
public sealed class VideoEncodingClauseTests
{
    private static ResolvedVideoEncoding Resolution(Guid worker, VideoEncoderDescriptor encoder, EncodingProfileSnapshot profile, EncodingProfileTarget target) =>
        new("encode", worker, encoder.Codec, encoder.Id, "7.0", encoder.Acceleration, encoder.QualityMode, encoder.QualityOption, target.Quality,
            target.Preset, string.IsNullOrEmpty(encoder.PresetOption) ? null : encoder.PresetOption,
            encoder.Presets.FirstOrDefault(item => item.Id == target.Preset)?.Value, profile.Id, profile.Version, profile.Name, target.Priority, DateTimeOffset.UnixEpoch);

    private static (VideoEncoderDescriptor Encoder, EncodingProfileSnapshot Profile, EncodingProfileTarget Target) Fixture()
    {
        var encoder = VideoEncoderCatalog.All.First(item => item.Acceleration == "cpu" && item.Presets.Count > 0);
        var target = new EncodingProfileTarget(encoder.Id, encoder.DefaultQuality, encoder.Presets[0].Id, 1);
        return (encoder, new EncodingProfileSnapshot(Guid.NewGuid(), 1, "Profil", "", [target]), target);
    }

    [Fact]
    public void WorkerClausesAreNamed()
    {
        var (encoder, profile, target) = Fixture();
        var worker = Guid.NewGuid();
        var resolution = Resolution(worker, encoder, profile, target);
        Assert.Null(RuntimeRepository.WorkerClause(resolution, worker));
        Assert.StartsWith("worker : attendu aucun", RuntimeRepository.WorkerClause(resolution, null));
        Assert.Contains(worker.ToString(), RuntimeRepository.WorkerClause(resolution, Guid.NewGuid()));
        Assert.Equal("version_ffmpeg : vide", RuntimeRepository.WorkerClause(resolution with { FfmpegVersion = " " }, worker));
        Assert.StartsWith("version_ffmpeg : longueur 129", RuntimeRepository.WorkerClause(resolution with { FfmpegVersion = new string('x', 129) }, worker));
        Assert.Equal("resolved_at : valeur par défaut", RuntimeRepository.WorkerClause(resolution with { ResolvedAt = default }, worker));
    }

    [Fact]
    public void FieldClausesGiveExpectedAndReceivedValues()
    {
        var (encoder, profile, target) = Fixture();
        var resolution = Resolution(Guid.NewGuid(), encoder, profile, target);
        var preset = encoder.Presets.First(item => item.Id == target.Preset).Value;
        Assert.Null(RuntimeRepository.FieldClause(resolution, profile, encoder, target, encoder.Codec, preset));
        Assert.StartsWith("profile :", RuntimeRepository.FieldClause(resolution, null, encoder, target, encoder.Codec, preset));
        Assert.StartsWith("encoder :", RuntimeRepository.FieldClause(resolution, profile, null, target, encoder.Codec, preset));
        Assert.StartsWith("target :", RuntimeRepository.FieldClause(resolution, profile, encoder, null, encoder.Codec, preset));
        Assert.Equal($"codec : attendu av2, reçu {encoder.Codec}", RuntimeRepository.FieldClause(resolution, profile, encoder, target, "av2", preset));
        Assert.Equal($"profile_version : attendu 1, reçu 9", RuntimeRepository.FieldClause(resolution with { ProfileVersion = 9 }, profile, encoder, target, encoder.Codec, preset));
        Assert.Equal($"priority : attendu 1, reçu 5", RuntimeRepository.FieldClause(resolution with { Priority = 5 }, profile, encoder, target, encoder.Codec, preset));
        Assert.StartsWith("preset_id : attendu", RuntimeRepository.FieldClause(resolution with { PresetId = "autre" }, profile, encoder, target, encoder.Codec, preset));
        Assert.Equal("preset_value : attendu " + preset + ", reçu null", RuntimeRepository.FieldClause(resolution with { PresetValue = null }, profile, encoder, target, encoder.Codec, preset));
    }

    [Fact]
    public void NodeClausesAreNamed()
    {
        Assert.Equal("node : nœud introuvable dans le flow", RuntimeRepository.NodeClause(null));
    }
}
