namespace Donna.Core;

/// <summary>
/// Journal de diagnostic optionnel (réglage "Activer les logs" dans Avancé) —
/// écrit dans %APPDATA%\Donna\logs\donna.log. Désactivé par défaut : DONNA
/// observe toute la frappe système, on ne journalise rien sans consentement explicite.
/// </summary>
public static class DiagnosticLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Donna", "logs", "donna.log");

    public static bool Enabled { get; set; }

    public static void LogException(Exception ex)
    {
        if (!Enabled)
            return;

        try
        {
            string? directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // Le logging est un confort de diagnostic, pas critique : une erreur d'écriture
            // (disque plein, dossier verrouillé...) ne doit jamais faire planter DONNA.
        }
    }
}
