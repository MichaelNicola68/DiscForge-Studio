using System.Text.RegularExpressions;

namespace DiscForgeStudio.Services;

public static class UserFriendlyErrorService
{
    public static string Explain(string? technicalMessage)
    {
        var message = Normalize(technicalMessage);

        if (Contains(message, "NO_COMPATIBLE_MULTISESSION_TYPE") ||
            Contains(message, "keinen der Multisessiontypen"))
            return "Diese Disc enthält keine kompatible offene Multisession. " +
                   "Die Disc ist möglicherweise leer, endgültig abgeschlossen oder verwendet ein nicht unterstütztes Sitzungsformat. " +
                   "Die Disc wurde nicht verändert.";

        if (Contains(message, "nicht leer") || Contains(message, "MediaHeuristicallyBlank"))
            return "Diese Disc enthält bereits Daten. Für eine neue Daten-Disc wird ein leeres Medium benötigt. " +
                   "Zum Erweitern verwenden Sie den Multisession Inspector. Die Disc wurde nicht verändert.";

        if (Contains(message, "EMPTY_DISC"))
            return "Die eingelegte Disc ist leer. Für diese Funktion wird eine bereits beschriebene Disc benötigt.";

        if (Contains(message, "NO_SUPPORTED_FILE_SYSTEM"))
            return "Auf der Disc wurde kein unterstütztes Dateisystem gefunden. Die Disc wurde nicht verändert.";

        return string.IsNullOrWhiteSpace(message)
            ? "Der Vorgang konnte nicht abgeschlossen werden. Die Disc wurde nicht verändert."
            : message;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Replace("unterstützt", "unterstützt")
                        .Replace("Fuege", "Füge")
                        .Replace("angehaengt", "angeh\u00E4ngt")
                        .Replace("Schreibgeschuetzte", "Schreibgesch\u00FCtzte");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static bool Contains(string source, string value) =>
        source.Contains(value, StringComparison.OrdinalIgnoreCase);
}