using Donna.Ai;
using Donna.Config;
using Donna.Core;
using Donna.Input;
using Donna.Ui;
using Interop.UIAutomationClient;

namespace Donna;

/// <summary>
/// Chef d'orchestre de DONNA : relie hooks, buffer, clients IA et injecteur,
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
    private readonly VerifiedFieldWriter _verifiedWriter;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripItem _undoMenuItem;
    private readonly PillOverlay _pill = new();

    private TypingBuffer _buffer;
    private KeyRing? _keyRing;
    private string _model;

    // Dernière transformation écrite via UI Automation (mode 2), pour "Annuler"
    // (menu du tray) — seulement pour ce chemin : c'est le seul où on a déjà en
    // main le texte d'origine ET une référence stable vers l'élément ciblé.
    // CurrentText = ce que le champ contient MAINTENANT (la réponse injectée),
    // utilisé comme dernier recours si le repli clavier d'Annuler doit
    // lui-même effacer quelque chose. PreviousText = ce qu'Annuler doit remettre.
    // On ne garde qu'une seule transformation (pas d'historique), et on ne
    // restaure QUE dans ce même élément, jamais dans le champ qui a le focus
    // au moment d'Annuler.
    private (IUIAutomationElement Element, string CurrentText, string PreviousText)? _lastTransformation;

    public DonnaContext()
    {
        _verifiedWriter = new VerifiedFieldWriter(_uiaAccessor, _injector);

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

        (_trayIcon, _undoMenuItem) = CreateTrayIcon();
    }

    private static KeyRing? TryCreateKeyRing(AppConfig config)
    {
        if (config.EncryptedApiKeys.Count == 0)
            return null;

        List<string> keys = config.EncryptedApiKeys.Select(DpapiSecret.Unprotect).ToList();
        return new KeyRing(keys);
    }

    private (NotifyIcon, ToolStripItem) CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Réglages...", null, (_, _) => OpenSettings());

        ToolStripItem undoItem = menu.Items.Add(
            "Annuler la dernière transformation", null, async (_, _) => await UndoLastTransformationAsync());
        undoItem.Enabled = false; // rien à annuler tant qu'aucune transformation (mode 2) n'a réussi

        menu.Items.Add("Quitter", null, (_, _) => ExitDonna());

        Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        var trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "DONNA",
            ContextMenuStrip = menu,
            Visible = true,
        };

        return (trayIcon, undoItem);
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

        try
        {
            // Une source vide recouvre deux situations différentes : texte
            // collé/déjà présent à lire (mode 2), ou aucune source voulue du
            // tout (mode 3, génération pure). On tente donc TOUJOURS une
            // lecture UI Automation quand rien n'est tapé — mais un échec de
            // cette lecture (application non supportée, curseur ailleurs...)
            // ne bloque JAMAIS rien : TransformModeSelector fait simplement
            // retomber sur le mode 3, qui fonctionne partout via TextInjector.
            UiaFieldAccessor.ReadResult? read = null;
            if (trigger.Source.Length == 0)
                read = await Task.Run(() => _uiaAccessor.TryReadFocusedField(trigger.TypedSuffix));

            (TransformMode mode, string source) = TransformModeSelector.SelectMode(trigger, read?.Text);

            string reply = await GenerateWithKeyRotationAsync(source, trigger.Prompt);
            string cleaned = ResponseCleaner.Clean(reply);

            if (mode == TransformMode.UiaSource)
            {
                IUIAutomationElement element = read!.Element;
                string originalFieldText = read.Text;

                // Écriture à deux niveaux : SetValue en priorité (atomique,
                // vérifié), repli clavier sinon — un seul appel
                // TextInjector.Replace (Backspace + caractères dans le même
                // SendInput, comme le mode 1), puis une vérification finale
                // FACULTATIVE par relecture fraîche du focus. Voir
                // VerifiedFieldWriter : aucune boucle effacer-vérifier-
                // réessayer, pour ne jamais risquer de double effacement sur
                // les applications où une référence UI Automation mémorisée
                // devient périmée (WhatsApp Web). Appelée directement via
                // await (pas de Task.Run ici) : Write est async et encapsule
                // elle-même chacun de ses appels UI Automation dans un
                // Task.Run pour rester sur un thread MTA.
                bool verified = await _verifiedWriter.Write(element, originalFieldText, cleaned);

                // Mémorisée dans tous les cas, même si la vérification est
                // incertaine : c'est "Annuler" qui est le vrai filet de
                // sécurité ici, pas cette vérification — l'écriture a de toute
                // façon déjà eu lieu, la griser priverait l'utilisateur de son
                // seul recours.
                _lastTransformation = (element, cleaned, source);
                _undoMenuItem.Enabled = true;

                if (verified)
                    _pill.ShowSuccess();
                else
                    _pill.ShowWarning("Écrit, mais impossible de confirmer — vérifie le champ.");
            }
            else
            {
                // Mode 1 (source tapée) et mode 3 (génération pure) : même
                // injection classique, qui fonctionne partout — CharsToDelete
                // couvre exactement la bonne longueur dans les deux cas (toute
                // la formule tapée, avec ou sans source).
                _injector.Replace(trigger.CharsToDelete, cleaned);
                _pill.ShowSuccess();
            }
        }
        catch (Exception ex)
        {
            // Échec avant toute écriture (quota, réseau, clé invalide...) : le
            // texte tapé n'est jamais effacé sans réponse à injecter à la
            // place, donc rien n'est perdu ici.
            DiagnosticLog.LogException(ex);
            _pill.ShowError(ex.Message);
        }
    }

    private async Task UndoLastTransformationAsync()
    {
        if (_lastTransformation is not { } state)
            return; // défense en profondeur : le menu est grisé dans ce cas

        try
        {
            // Même chemin d'écriture que le mode 2 (SetValue puis repli
            // clavier) : Annuler doit fonctionner là où la transformation
            // elle-même a eu besoin du repli clavier (WhatsApp Web, Word...),
            // pas seulement là où SetValue marche.
            bool verified = await _verifiedWriter.Write(state.Element, state.CurrentText, state.PreviousText);
            if (verified)
                _pill.ShowSuccess();
            else
                _pill.ShowWarning("Annulé, mais impossible de confirmer — vérifie le champ.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.LogException(ex);
            _pill.ShowError("Impossible d'annuler : " + ex.Message);
        }
        finally
        {
            // Un seul niveau d'annulation : qu'elle réussisse ou échoue, on ne
            // retente pas indéfiniment sur un état potentiellement incertain.
            _lastTransformation = null;
            _undoMenuItem.Enabled = false;
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
