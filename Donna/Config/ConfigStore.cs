using System.Text.Json;
using Donna.Input;

namespace Donna.Config;

/// <summary>Lecture/écriture de la configuration DONNA en JSON sur disque.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>Utilise l'emplacement par défaut : %APPDATA%\Donna\config.json.</summary>
    public ConfigStore() : this(DefaultPath())
    {
    }

    /// <summary>Utilise un chemin explicite (tests, import/export, config portable).</summary>
    public ConfigStore(string path)
    {
        _path = path;
    }

    private static string DefaultPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Donna", "config.json");
    }

    /// <summary>Charge la config, ou une config par défaut si le fichier n'existe pas encore (premier lancement).</summary>
    public AppConfig Load()
    {
        if (!File.Exists(_path))
            return new AppConfig();

        string json = File.ReadAllText(_path);
        AppConfig config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();

        // Mise en sécurité : la portée AllBeforeCursor (sélection clavier de tout le
        // texte avant le curseur) a détruit des documents entiers en conditions
        // réelles — une désélection après timeout peut être traitée par
        // l'application AVANT la sélection elle-même (SendInput est asynchrone),
        // laissant tout le document sélectionné sans rien pour le relâcher. On
        // force donc Line, quelle que soit la valeur enregistrée, tant qu'un
        // remplacement fiable (UI Automation, sans clavier ni sélection) n'a pas
        // remplacé ce mécanisme.
        if (config.SourceScope == SourceScope.AllBeforeCursor)
            config.SourceScope = SourceScope.Line;

        return config;
    }

    public void Save(AppConfig config)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_path, json);
    }
}
