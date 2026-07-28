namespace Donna.Config;

/// <summary>
/// Modèle de configuration persistant (%APPDATA%\Donna\config.json).
/// Voir ARCHITECTURE.md §5 Config/ et §8.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Clés API Gemini, chiffrées DPAPI (voir <see cref="DpapiSecret"/>) — jamais en clair sur le disque.</summary>
    public List<string> EncryptedApiKeys { get; set; } = [];

    /// <summary>Mot déclencheur (insensible à la casse) — voir ARCHITECTURE.md §6.</summary>
    public string TriggerWord { get; set; } = "donna";

    /// <summary>Modèle Gemini utilisé pour la génération.</summary>
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>Délai (ms) avant de restaurer le presse-papiers après le collage — voir TextInjector.</summary>
    public int PasteRestoreDelayMs { get; set; } = 150;

    /// <summary>Active des logs de diagnostic (pas encore implémentés — réglage sans effet pour l'instant).</summary>
    public bool LogsEnabled { get; set; }
}
