using Encoding.Contracts.Workers;

namespace Encoding.Runtime.Tests;

internal static class WorkerScheduleScenarios
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Slots(Func<int, int, bool> open)
    {
        var chars = new char[WorkerSchedule.SlotCount];
        for (var index = 0; index < chars.Length; index++)
            chars[index] = open(index / WorkerSchedule.SlotsPerDay, index % WorkerSchedule.SlotsPerDay) ? '1' : '0';
        return new string(chars);
    }

    public static void Run()
    {
        // Mardi 2026-10-13, 22:30 UTC = mercredi 00:30 à Paris (UTC+2 en octobre).
        var tuesdayNight = new DateTimeOffset(2026, 10, 13, 22, 30, 0, TimeSpan.Zero);
        Check(WorkerSchedule.AlwaysOpen.Validate() is null, "Le calendrier par défaut doit être valide.");
        Check(WorkerSchedule.AlwaysOpen.IsOpen(tuesdayNight), "Un calendrier désactivé est toujours ouvert.");

        var nightOnly = new WorkerSchedule(true, "UTC", Slots((_, quarter) => quarter < 6 * WorkerSchedule.SlotsPerHour));
        Check(nightOnly.Validate() is null, "Un calendrier de nuit doit être valide.");
        Check(!nightOnly.IsOpen(tuesdayNight), "22:30 UTC est hors du créneau de nuit.");
        Check(nightOnly.IsOpen(new DateTimeOffset(2026, 10, 13, 5, 45, 0, TimeSpan.Zero)), "05:45 UTC est dans le créneau.");
        Check(!nightOnly.IsOpen(new DateTimeOffset(2026, 10, 13, 6, 0, 0, TimeSpan.Zero)), "06:00 UTC est exclu.");

        var paris = nightOnly with { TimeZone = "Europe/Paris" };
        Check(paris.IsOpen(tuesdayNight), "00:30 à Paris est dans le créneau de nuit.");

        var wednesdayOnly = new WorkerSchedule(true, "UTC", Slots((day, _) => day == (int)DayOfWeek.Wednesday));
        Check(!wednesdayOnly.IsOpen(tuesdayNight), "Mardi est exclu d’un calendrier réservé au mercredi.");
        Check(wednesdayOnly.IsOpen(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero)), "Mercredi midi est autorisé.");
        Check(paris with { Slots = wednesdayOnly.Slots } is { } shifted && shifted.IsOpen(tuesdayNight), "Le jour se calcule dans le fuseau du worker.");

        var sundayFirst = new WorkerSchedule(true, "UTC", Slots((day, _) => day == 0));
        Check(sundayFirst.IsOpen(new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero)), "Le premier créneau est dimanche 00:00.");

        Check(nightOnly with { Slots = "101" } is { } shortSlots && shortSlots.Validate() is not null, "Une longueur incorrecte est refusée.");
        Check(nightOnly with { Slots = new string('2', WorkerSchedule.SlotCount) } is { } badChars && badChars.Validate() is not null, "Un caractère inconnu est refusé.");
        Check(nightOnly with { TimeZone = "Nowhere/Land" } is { } badZone && badZone.Validate() is not null, "Un fuseau inconnu est refusé.");
        Check(new WorkerSchedule(true, "UTC", new string('0', WorkerSchedule.SlotCount)).Validate() is not null, "Un calendrier activé sans créneau est refusé.");
        Check(!(new WorkerSchedule(true, "Nowhere/Land", nightOnly.Slots)).IsOpen(tuesdayNight), "Un calendrier invalide ne démarre rien.");
        Console.WriteLine("PASS: calendrier hebdomadaire des workers (créneaux, fuseau, validation).");
    }
}
