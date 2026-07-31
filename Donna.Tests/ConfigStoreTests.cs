using Donna.Config;
using Donna.Input;
using Xunit;

namespace Donna.Tests;

public class ConfigStoreTests
{
    private static string TempConfigPath() => Path.Combine(Path.GetTempPath(), $"donna-test-{Guid.NewGuid()}.json");

    [Fact]
    public void Load_sans_fichier_renvoie_une_config_par_defaut()
    {
        var store = new ConfigStore(TempConfigPath());

        AppConfig config = store.Load();

        Assert.Equal("donna", config.TriggerWord);
        Assert.Empty(config.EncryptedApiKeys);
        Assert.Equal(SourceScope.Line, config.SourceScope);
    }

    [Fact]
    public void Save_puis_Load_redonne_les_memes_valeurs()
    {
        string path = TempConfigPath();
        try
        {
            var store = new ConfigStore(path);
            var original = new AppConfig
            {
                EncryptedApiKeys = ["blob-chiffre-1", "blob-chiffre-2"],
                TriggerWord = "assistant",
                Model = "gemini-flash-latest",
                LogsEnabled = true,
                SourceScope = SourceScope.Line,
            };

            store.Save(original);
            AppConfig reloaded = store.Load();

            Assert.Equal(original.EncryptedApiKeys, reloaded.EncryptedApiKeys);
            Assert.Equal(original.TriggerWord, reloaded.TriggerWord);
            Assert.Equal(original.Model, reloaded.Model);
            Assert.Equal(original.LogsEnabled, reloaded.LogsEnabled);
            Assert.Equal(original.SourceScope, reloaded.SourceScope);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_force_Line_meme_si_AllBeforeCursor_est_enregistre()
    {
        // Mise en sécurité : AllBeforeCursor a causé une perte de données réelle
        // (sélection clavier restée active après un échec). Load() doit toujours
        // ramener à Line, quelle que soit la valeur présente sur disque — y compris
        // pour un fichier de config plus ancien qui aurait encore AllBeforeCursor.
        string path = TempConfigPath();
        try
        {
            var store = new ConfigStore(path);
            store.Save(new AppConfig { SourceScope = SourceScope.AllBeforeCursor });

            AppConfig reloaded = store.Load();

            Assert.Equal(SourceScope.Line, reloaded.SourceScope);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Save_cree_le_dossier_parent_si_absent()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"donna-test-dir-{Guid.NewGuid()}");
        string path = Path.Combine(dir, "config.json");

        try
        {
            var store = new ConfigStore(path);
            store.Save(new AppConfig());

            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
