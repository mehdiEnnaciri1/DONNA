using Donna.Input;

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

    /// <summary>Active la journalisation de diagnostic dans %APPDATA%\Donna\logs\donna.log — voir DiagnosticLog.</summary>
    public bool LogsEnabled { get; set; }

    /// <summary>
    /// Portée de la sélection utilisée par <see cref="Input.SelectionReader"/> quand
    /// aucune source n'a été tapée au clavier (texte collé ou déjà présent dans le champ).
    /// </summary>
    public SourceScope SourceScope { get; set; } = SourceScope.Line;
}
