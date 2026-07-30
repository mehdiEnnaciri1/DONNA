using Donna.Ai;
using Donna.Config;
using Donna.Core;
using Donna.Input;
using Donna.Ui;

namespace Donna;

/// <summary>
/// Chef d'orchestre de DONNA : relie hooks, buffer, client Gemini et injecteur,
/// charge/sauvegarde la configuration (clés API chiffrées DPAPI comprises), et
/// gère l'icône de barre des tâches (menu Réglages / Quitter).
/// </summary>
public sealed class DonnaContext : ApplicationContext
{
    private const uint VK_BACK = 0x08;
    private const uint VK_TAB = 0x09;
    private const uint VK_RETURN = 0x0D;
    private const uint VK_ESCAPE = 0x1B;
    private const uint VK_END = 0x23;
    private const uint VK_HOME = 0x24;
    private const uint VK_LEFT = 0x25;
    private const uint VK_UP = 0x26;
    private const uint VK_RIGHT = 0x27;
    private const uint VK_DOWN = 0x28;
    private const uint VK_V = 0x56;

    // Touches qui réinitialisent le buffer par prudence (clic et changement de
    // fenêtre sont gérés séparément par MouseHook/ForegroundWatcher) — voir
    // ARCHITECTURE.md §6 : « Entrée, Échap, Tab, flèches, Origine/Fin ».
    private static readonly HashSet<uint> ResetKeys =
        [VK_RETURN, VK_ESCAPE, VK_TAB, VK_HOME, VK_END, VK_LEFT, VK_UP, VK_RIGHT, VK_DOWN];

    private readonly ConfigStore _configStore = new();
    private readonly KeyboardHook _keyboardHook = new();
    private readonly MouseHook _mouseHook = new();
    private readonly ForegroundWatcher _foregroundWatcher = new();
    private readonly KeyTranslator _translator = new();
    private readonly GeminiClient _gemini = new();
    private readonly GroqClient _groq = new();
    private readonly TextInjector _injector = new();
    private readonly SelectionReader _selectionReader = new();
    private readonly NotifyIcon _trayIcon;
    private readonly PillOverlay _pill = new();

    private TypingBuffer _buffer;
    private KeyRing? _keyRing;
    private string _model;
    private SourceScope _sourceScope;

    public DonnaContext()
    {
        AppConfig config = _configStore.Load();

        _buffer = new TypingBuffer(config.TriggerWord);
        _model = config.Model;
        _sourceScope = config.SourceScope;
        _keyRing = TryCreateKeyRing(config);
        DiagnosticLog.Enabled = config.LogsEnabled;

        _keyboardHook.KeyDown += OnKeyDown;
        _keyboardHook.KeyUp += OnKeyUp;
        _mouseHook.Click += () => _buffer.Reset();
        _foregroundWatcher.ForegroundChanged += () => _buffer.Reset();

        _keyboardHook.Install();
        _mouseHook.Install();
        _foregroundWatcher.Install();

        _trayIcon = CreateTrayIcon();
    }

    private static KeyRing? TryCreateKeyRing(AppConfig config)
    {
        if (config.EncryptedApiKeys.Count == 0)
            return null;

        List<string> keys = config.EncryptedApiKeys.Select(DpapiSecret.Unprotect).ToList();
        return new KeyRing(keys);
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Réglages...", null, (_, _) => OpenSettings());
        menu.Items.Add("Quitter", null, (_, _) => ExitDonna());

        Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        return new NotifyIcon
        {
            Icon = icon,
            Text = "DONNA",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private void OpenSettings()
    {
        AppConfig config = _configStore.Load();
        List<string> decryptedKeys = config.EncryptedApiKeys.Select(DpapiSecret.Unprotect).ToList();

        using var form = new SettingsForm(config, decryptedKeys, Autostart.IsEnabled());
        if (form.ShowDialog() != DialogResult.OK)
            return;

        AppConfig updated = form.ToConfig();
        updated.EncryptedApiKeys = form.DecryptedApiKeys.Select(DpapiSecret.Protect).ToList();

        _configStore.Save(updated);
        Autostart.SetEnabled(form.AutostartEnabled);

        // Applique les nouveaux réglages sans redémarrer DONNA.
        _buffer = new TypingBuffer(updated.TriggerWord);
        _model = updated.Model;
        _sourceScope = updated.SourceScope;
        _keyRing = TryCreateKeyRing(updated);
        DiagnosticLog.Enabled = updated.LogsEnabled;
    }

    private void OnKeyDown(KeyEvent evt)
    {
        // Ignore les touches injectées par TextInjector/SelectionReader eux-mêmes
        // (leurs propres Backspace, frappes Unicode, Maj/Ctrl+Origine, Ctrl+C, Fin
        // repassent par ce même hook) — sinon boucle de rétroaction et pollution du buffer.
        if (evt.IsInjected)
            return;

        bool wasControlDown = _translator.IsControlDown;
        _translator.OnKeyDown(evt.VkCode);

        if (evt.VkCode == VK_BACK)
        {
            _buffer.Backspace();
            return;
        }

        // Collage manuel (Ctrl+V) : le champ a pu changer sous nos pieds, reset par prudence.
        if (evt.VkCode == VK_V && wasControlDown)
        {
            _buffer.Reset();
            return;
        }

        if (ResetKeys.Contains(evt.VkCode))
        {
            _buffer.Reset();
            return;
        }

        string text = _translator.Translate(evt.VkCode, evt.ScanCode);
        if (text.Length == 0)
            return;

        var match = _buffer.Append(text);
        if (match is { } trigger)
            _ = ProcessTriggerAsync(trigger); // fire-and-forget : ne jamais bloquer le hook clavier (synchrone, tout le système)
    }

    private void OnKeyUp(KeyEvent evt)
    {
        if (!evt.IsInjected)
            _translator.OnKeyUp(evt.VkCode);
    }

    private async Task ProcessTriggerAsync(TriggerMatch trigger)
    {
        // Pas de ConfigureAwait(false) ici, volontairement : _pill (PillOverlay) est
        // un contrôle WinForms, et SelectionReader.ReadSelection fait des appels OLE
        // (Clipboard) — les deux exigent le thread UI STA. Rester sur le contexte de
        // synchronisation WinForms garantit qu'on y reprend après l'appel réseau,
        // jamais sur un thread du pool.
        _pill.ShowSending();

        // TypingBuffer ne voit ni le texte collé (Ctrl+V réinitialise le buffer par
        // prudence) ni le texte déjà présent dans le champ avant que DONNA démarre.
        // Repli : source vide → on efface juste ce qu'on vient de taper (le
        // déclencheur + l'instruction), puis on lit le texte réel par sélection +
        // copie (SelectionReader) plutôt que d'appeler l'IA avec une source vide.
        bool usingSelectionFallback = trigger.Source.Length == 0;

        try
        {
            string source = trigger.Source;

            if (usingSelectionFallback)
            {
                _injector.Replace(trigger.TriggerLength, "");

                // Appel STRICTEMENT synchrone, sans Task.Run : ReadSelection fait des
                // appels OLE (Clipboard) qui exigent le thread UI STA courant. Un
                // Task.Run l'exécuterait sur un thread du pool (MTA) et ferait échouer
                // ces appels. Ce blocage synchrone (jusqu'à ~500 ms) du thread du hook
                // clavier est le compromis accepté pour ce chemin de repli, rare.
                source = _selectionReader.ReadSelection(_sourceScope);
                if (source.Length == 0)
                    throw new InvalidOperationException("Aucun texte à transformer : le champ est vide."); // le catch ci-dessous désélectionne
            }

            string reply = await GenerateWithKeyRotationAsync(source, trigger.Prompt);
            string cleaned = ResponseCleaner.Clean(reply);

            // Repli : la sélection Ctrl+C est encore active, la 1re frappe la
            // remplace — pas de Backspace à envoyer. Chemin normal : on efface
            // toute la formule tapée avant d'injecter, comme avant.
            _injector.Replace(usingSelectionFallback ? 0 : trigger.CharsToDelete, cleaned);
            _pill.ShowSuccess();
        }
        catch (Exception ex)
        {
            // Échec (quota, réseau, clé invalide, lecture de la sélection...) : on
            // ne détruit jamais le texte réel de l'utilisateur. Chemin normal : on
            // n'efface rien, la formule tapée reste visible. Repli sélection : le
            // déclencheur tapé a déjà été effacé plus haut (nécessaire pour pouvoir
            // sélectionner le texte réel) ; on désélectionne juste proprement pour
            // que ce texte réel reste intact et visible, sans rien y coller.
            DiagnosticLog.LogException(ex);
            _pill.ShowError(ex.Message);

            if (usingSelectionFallback)
            {
                try
                {
                    _selectionReader.Deselect();
                }
                catch
                {
                    // Best effort : ne jamais remplacer l'erreur déjà journalisée et
                    // affichée par un second échec sur la désélection elle-même.
                }
            }
        }
    }

    /// <summary>
    /// Essaie chaque clé du trousseau à tour de rôle jusqu'à ce qu'une réponde,
    /// en appelant Gemini ou Groq selon le fournisseur détecté pour chaque clé
    /// (voir <see cref="AiProviderDetector"/>) — le trousseau peut mélanger des
    /// clés de plusieurs fournisseurs. Bascule sur la clé suivante pour
    /// n'importe quel échec (quota, clé invalide, mauvais fournisseur...), pas
    /// seulement le quota : DONNA n'a aucune garantie a priori qu'une clé donnée
    /// fonctionne.
    /// </summary>
    private async Task<string> GenerateWithKeyRotationAsync(string source, string prompt)
    {
        if (_keyRing is null)
            throw new InvalidOperationException("Aucune clé API configurée. Ouvre Réglages pour en ajouter une.");

        Exception? lastFailure = null;

        while (true)
        {
            if (_keyRing.CurrentKey is not { } apiKey)
            {
                throw lastFailure is null
                    ? new InvalidOperationException("Aucune clé API configurée. Ouvre Réglages pour en ajouter une.")
                    : new InvalidOperationException(
                        "Toutes les clés API ont échoué. Vérifie-les dans Réglages.", lastFailure);
            }

            try
            {
                return AiProviderDetector.Detect(apiKey) switch
                {
                    AiProvider.Groq => await _groq.GenerateAsync(apiKey, source, prompt),
                    _ => await _gemini.GenerateAsync(apiKey, _model, source, prompt),
                };
            }
            catch (Exception ex) when (ex is AiQuotaExceededException or AiApiException)
            {
                lastFailure = ex;
                _keyRing.MarkCurrentAsFailed();
                // On boucle : soit une clé suivante est disponible et on la
                // réessaie, soit CurrentKey vaudra null et la garde du haut
                // lèvera l'erreur (avec la dernière raison d'échec) au tour suivant.
            }
        }
    }

    private void ExitDonna()
    {
        _trayIcon.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _keyboardHook.Dispose();
            _mouseHook.Dispose();
            _foregroundWatcher.Dispose();
            _gemini.Dispose();
            _groq.Dispose();
            _trayIcon.Dispose();
            _pill.Dispose();
        }

        base.Dispose(disposing);
    }
}
