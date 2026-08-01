using Donna.Ai;
using Donna.Config;
using Donna.Core;
using Donna.Input;
using Donna.Ui;
using Interop.UIAutomationClient;

namespace Donna;

/// <summary>
/// Chef d'orchestre de DONNA : relie hooks, buffer, client Gemini et injecteur,
/// charge/sauvegarde la configuration (clés API chiffrées DPAPI comprises), et
/// gère l'icône de barre des tâches (menu Réglages / Annuler / Quitter).
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
    private readonly UiaFieldAccessor _uiaAccessor = new();
    private readonly NotifyIcon _trayIcon;
    private readonly PillOverlay _pill = new();

    private TypingBuffer _buffer;
    private KeyRing? _keyRing;
    private string _model;

    // Dernière transformation écrite via UI Automation, pour "Annuler" (menu du
    // tray) — seulement pour ce chemin : c'est le seul où on a déjà en main le
    // texte d'origine ET une référence stable vers l'élément ciblé. On ne garde
    // que la dernière (pas d'historique) et on ne restaure QUE dans ce même
    // élément, jamais dans le champ qui a le focus au moment d'Annuler.
    private (IUIAutomationElement Element, string PreviousText)? _lastTransformation;

    public DonnaContext()
    {
        AppConfig config = _configStore.Load();

        _buffer = new TypingBuffer(config.TriggerWord);
        _model = config.Model;
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
        menu.Items.Add("Annuler la dernière transformation", null, async (_, _) => await UndoLastTransformationAsync());
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
        _keyRing = TryCreateKeyRing(updated);
        DiagnosticLog.Enabled = updated.LogsEnabled;
    }

    private void OnKeyDown(KeyEvent evt)
    {
        // Ignore les touches injectées par TextInjector lui-même (ses propres
        // Backspace/frappes Unicode repassent par ce même hook) — sinon boucle
        // de rétroaction et pollution du buffer. UiaFieldAccessor n'injecte
        // jamais de touche, donc rien à filtrer de ce côté-là.
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
        // Pas de ConfigureAwait(false) ici, volontairement : _pill (PillOverlay)
        // est un contrôle WinForms, ses méthodes doivent s'exécuter sur le
        // thread UI. Rester sur le contexte de synchronisation WinForms
        // garantit qu'on y reprend après chaque `await` (réseau, ou Task.Run
        // UI Automation), jamais sur un thread du pool livré à lui-même.
        _pill.ShowSending();

        // TypingBuffer ne voit ni le texte collé (Ctrl+V réinitialise le buffer
        // par prudence) ni le texte déjà présent dans le champ avant que DONNA
        // démarre. Repli : source vide → lecture du champ réel via UI
        // Automation (UiaFieldAccessor), sans jamais injecter de touche, toucher
        // au presse-papiers, ni créer de sélection — contrairement à l'ancienne
        // lecture par sélection clavier, qui a détruit des documents entiers.
        bool usingUiaFallback = trigger.Source.Length == 0;
        IUIAutomationElement? targetElement = null;

        try
        {
            string source = trigger.Source;

            if (usingUiaFallback)
            {
                // UI Automation est recommandé par Microsoft depuis un thread MTA
                // (à l'inverse du presse-papiers, qui exigeait STA) — Task.Run
                // fournit un thread du pool, MTA par défaut. Rien ne dépend ici du
                // pompage de messages du hook clavier (aucune touche injectée),
                // donc pas de risque d'interblocage à surveiller sur ce point.
                UiaFieldAccessor.ReadResult? read = await Task.Run(() => _uiaAccessor.TryReadFocusedField(trigger.TypedSuffix));
                if (read is null)
                {
                    throw new InvalidOperationException(
                        "Lecture du champ non supportée par cette application (ex. l'éditeur de VS Code). " +
                        "Tape ton texte avant le déclencheur pour cette application.");
                }

                // Le champ contient encore la formule tapée (rien n'a été effacé :
                // on ne touche au champ qu'au moment d'écrire, plus bas) — on la
                // retire par découpage de chaîne, sans envoyer le moindre Backspace.
                // On VÉRIFIE d'abord que le texte lu se termine bien par exactement
                // ce que DONNA a vu taper (TypedSuffix), plutôt que de tronquer par
                // longueur : si le curseur n'était pas en fin de champ au moment du
                // déclenchement (ex. clic au milieu d'un document déjà rempli), une
                // troncature aveugle couperait du vrai contenu et laisserait la
                // formule elle-même dans la source envoyée à l'IA.
                if (!trigger.TryExtractSourceFromFieldText(read.Text, out source))
                {
                    throw new InvalidOperationException(
                        "Le curseur n'était pas en fin de champ au moment du déclenchement. " +
                        "Place-le à la fin, ou tape ta source avant \"donna\".");
                }

                if (source.Length == 0)
                    throw new InvalidOperationException("Aucun texte à transformer : le champ est vide.");

                targetElement = read.Element;
            }

            string reply = await GenerateWithKeyRotationAsync(source, trigger.Prompt);
            string cleaned = ResponseCleaner.Clean(reply);

            if (usingUiaFallback)
            {
                bool written = await Task.Run(() => _uiaAccessor.TryWrite(targetElement!, cleaned));
                if (!written)
                {
                    throw new InvalidOperationException(
                        "Cette application ne supporte pas l'écriture automatique du résultat " +
                        "(le champ n'a pas confirmé la nouvelle valeur).");
                }

                _lastTransformation = (targetElement!, source);
            }
            else
            {
                _injector.Replace(trigger.CharsToDelete, cleaned);
            }

            _pill.ShowSuccess();
        }
        catch (Exception ex)
        {
            // Échec (quota, réseau, clé invalide, champ non supporté, écriture
            // refusée...) : dans les deux chemins, rien n'a été détruit — le
            // chemin normal n'efface qu'après un succès, et le chemin UI
            // Automation ne modifie le champ qu'au moment d'écrire (jamais avant).
            DiagnosticLog.LogException(ex);
            _pill.ShowError(ex.Message);
        }
    }

    private async Task UndoLastTransformationAsync()
    {
        if (_lastTransformation is not { } state)
        {
            _pill.ShowError("Rien à annuler.");
            return;
        }

        try
        {
            bool restored = await Task.Run(() => _uiaAccessor.TryWrite(state.Element, state.PreviousText));
            if (restored)
            {
                _lastTransformation = null;
                _pill.ShowSuccess();
            }
            else
            {
                _pill.ShowError("Impossible d'annuler : le champ visé n'est plus disponible ou a changé.");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.LogException(ex);
            _pill.ShowError("Impossible d'annuler : " + ex.Message);
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
