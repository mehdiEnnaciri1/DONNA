using System.Text.Json;

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
        return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
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
