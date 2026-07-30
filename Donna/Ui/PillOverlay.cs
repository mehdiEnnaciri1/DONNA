namespace Donna.Ui;

/// <summary>
/// Pastille flottante affichant l'état d'une requête Gemini (⏳ envoi / ✅
/// succès / ❌ erreur), sans jamais voler le focus : WS_EX_NOACTIVATE +
/// <see cref="ShowWithoutActivation"/> garantissent que l'afficher ne change
/// jamais la fenêtre active ni où part le texte injecté par TextInjector.
/// </summary>
public sealed class PillOverlay : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _hideTimer;

    public PillOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.FromArgb(32, 32, 32);
        Padding = new Padding(10, 6, 10, 6);

        _label = new Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f),
            Location = new Point(Padding.Left, Padding.Top),
            Text = "",
        };
        Controls.Add(_label);

        _hideTimer = new System.Windows.Forms.Timer();
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    /// <summary>Requête en cours — reste affichée jusqu'à ShowSuccess/ShowError.</summary>
    public void ShowSending() => Display("⏳ Envoi en cours…", autoHideMs: null);

    /// <summary>
    /// Une sélection réelle (texte du champ, pas la formule de DONNA) est active en
    /// arrière-plan — repli par sélection (voir SelectionReader). Toute frappe de
    /// l'utilisateur remplacerait cette sélection : reste affichée jusqu'à
    /// ShowSuccess/ShowError, qui marquent la fin de la fenêtre à risque.
    /// </summary>
    public void ShowSelectionActive() => Display("✋ Texte sélectionné — ne tapez pas…", autoHideMs: null);

    /// <summary>Réponse injectée avec succès — disparaît toute seule.</summary>
    public void ShowSuccess() => Display("✅", autoHideMs: 1200);

    /// <summary>Échec — reste un peu plus longtemps pour être lisible.</summary>
    public void ShowError(string message) => Display($"❌ {message}", autoHideMs: 4000);

    private void Display(string text, int? autoHideMs)
    {
        _hideTimer.Stop();
        _label.Text = text;

        var cursor = Cursor.Position;
        Location = new Point(cursor.X + 16, cursor.Y + 16);

        if (!Visible)
            Show();

        if (autoHideMs is int ms)
        {
            _hideTimer.Interval = ms;
            _hideTimer.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hideTimer.Dispose();

        base.Dispose(disposing);
    }
}
