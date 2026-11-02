namespace Encoding.Contracts.Nodes;

/// <summary>
/// Calendrier hebdomadaire d’un nœud : un créneau de 15 minutes par caractère, de dimanche 00:00 à samedi 23:45
/// dans le fuseau du nœud. Il ne bloque que le démarrage de nouveaux traitements.
/// </summary>
public sealed record NodeSchedule(bool Enabled, string TimeZone, string Slots)
{
    public const int SlotsPerHour = 4;
    public const int SlotsPerDay = 24 * SlotsPerHour;
    public const int SlotCount = 7 * SlotsPerDay;

    public static NodeSchedule AlwaysOpen { get; } = new(false, "UTC", new string('1', SlotCount));

    public string? Validate()
    {
        if (Slots is null || Slots.Length != SlotCount || Slots.Any(slot => slot is not ('0' or '1')))
            return $"Le calendrier doit contenir {SlotCount} créneaux de 15 minutes composés de 0 et de 1.";
        if (string.IsNullOrWhiteSpace(TimeZone) || TimeZone.Length > 64 || !TryFindTimeZone(TimeZone, out _))
            return "Le fuseau horaire du calendrier est inconnu.";
        if (Enabled && !Slots.Contains('1'))
            return "Un calendrier activé doit autoriser au moins un créneau.";
        return null;
    }

    /// <summary>Indique si un nouveau traitement peut démarrer à cet instant. Un calendrier désactivé est toujours ouvert.</summary>
    public bool IsOpen(DateTimeOffset instant)
    {
        if (!Enabled) return true;
        if (!TryFindTimeZone(TimeZone, out var zone) || Slots.Length != SlotCount) return false;
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return Slots[(int)local.DayOfWeek * SlotsPerDay + local.Hour * SlotsPerHour + local.Minute / 15] == '1';
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo zone)
    {
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; return false; }
    }
}
