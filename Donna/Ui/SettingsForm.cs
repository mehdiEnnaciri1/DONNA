using Donna.Config;

namespace Donna.Ui;

/// <summary>
/// Fenêtre de réglages à onglets (Clés API / Général / Avancé).
///
/// Reçoit la config actuelle en construction, la reflète dans les contrôles,
/// et expose <see cref="ToConfig"/> pour récupérer les valeurs éditées quand
/// l'appelant obtient <see cref="DialogResult.OK"/>. L'écriture sur disque
/// (chiffrement DPAPI + ConfigStore) et l'application du démarrage automatique
/// restent à la charge de l'appelant (DonnaContext) — cette fenêtre ne fait
/// que collecter les valeurs.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly TextBox _apiKeysTextBox;
    private readonly CheckBox _showKeysCheckBox;
    private readonly TextBox _triggerWordTextBox;
    private readonly CheckBox _autostartCheckBox;
    private readonly ComboBox _modelComboBox;
    private readonly NumericUpDown _pasteDelayNumericUpDown;
    private readonly CheckBox _logsEnabledCheckBox;

    /// <param name="config">Config actuelle à refléter dans les champs.</param>
    /// <param name="decryptedApiKeys">Clés déjà déchiffrées (jamais stocké ici en clair au-delà de l'affichage).</param>
    /// <param name="autostartCurrentlyEnabled">État réel du démarrage automatique (source de vérité : le registre, pas la config).</param>
    public SettingsForm(AppConfig config, IEnumerable<string> decryptedApiKeys, bool autostartCurrentlyEnabled)
    {
        Text = "DONNA — Réglages";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 320);

        var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var okButton = new Button
        {
            Text = "OK", DialogResult = DialogResult.OK,
            Location = new Point(228, 8), Width = 90,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        var cancelButton = new Button
        {
            Text = "Annuler", DialogResult = DialogResult.Cancel,
            Location = new Point(322, 8), Width = 90,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        buttonPanel.Controls.Add(okButton);
        buttonPanel.Controls.Add(cancelButton);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var apiKeysTab = new TabPage("Clés API");
        var apiKeysLabel = new Label
        {
            Text = "Une clé par ligne (Gemini et/ou Groq, détecté automatiquement). La " +
                   "première est essayée en priorité ; DONNA bascule sur la suivante en cas d'échec.",
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(0, 4, 0, 4),
        };
        _showKeysCheckBox = new CheckBox { Text = "Afficher les clés", Dock = DockStyle.Bottom };
        _apiKeysTextBox = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true, // sinon Entrée déclenche l'AcceptButton (OK) au lieu d'une nouvelle ligne
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            UseSystemPasswordChar = true,
            Text = string.Join(Environment.NewLine, decryptedApiKeys),
        };
        _showKeysCheckBox.CheckedChanged += (_, _) =>
            _apiKeysTextBox.UseSystemPasswordChar = !_showKeysCheckBox.Checked;
        apiKeysTab.Controls.Add(_apiKeysTextBox);
        apiKeysTab.Controls.Add(_showKeysCheckBox);
        apiKeysTab.Controls.Add(apiKeysLabel);

        var generalTab = new TabPage("Général");
        var triggerLabel = new Label { Text = "Mot déclencheur :", AutoSize = true, Location = new Point(12, 22) };
        _triggerWordTextBox = new TextBox { Text = config.TriggerWord, Location = new Point(150, 19), Width = 120 };
        _autostartCheckBox = new CheckBox
        {
            Text = "Démarrer automatiquement avec Windows",
            AutoSize = true,
            Location = new Point(12, 58),
            Checked = autostartCurrentlyEnabled,
        };
        var modelLabel = new Label { Text = "Modèle Gemini :", AutoSize = true, Location = new Point(12, 94) };
        _modelComboBox = new ComboBox
        {
            Location = new Point(150, 91),
            Width = 220,
            DropDownStyle = ComboBoxStyle.DropDown,
        };
        _modelComboBox.Items.AddRange(
        [
            "gemini-2.5-flash",
            "gemini-flash-latest",
            "gemini-flash-lite-latest",
            "gemini-pro-latest",
        ]);
        _modelComboBox.Text = config.Model;
        generalTab.Controls.AddRange([triggerLabel, _triggerWordTextBox, _autostartCheckBox, modelLabel, _modelComboBox]);

        var advancedTab = new TabPage("Avancé");
        var delayLabel = new Label { Text = "Délai avant collage (ms) :", AutoSize = true, Location = new Point(12, 22) };
        _pasteDelayNumericUpDown = new NumericUpDown
        {
            Location = new Point(190, 19),
            Width = 80,
            Minimum = 0,
            Maximum = 5000,
            Increment = 10,
            Value = config.PasteRestoreDelayMs,
        };
        _logsEnabledCheckBox = new CheckBox
        {
            Text = "Activer les logs (diagnostic)",
            AutoSize = true,
            Location = new Point(12, 58),
            Checked = config.LogsEnabled,
        };
        var keyboardLayoutLabel = new Label
        {
            Text = "Disposition clavier : détectée automatiquement selon la fenêtre active.",
            Location = new Point(12, 94),
            Size = new Size(380, 40),
        };
        advancedTab.Controls.AddRange([delayLabel, _pasteDelayNumericUpDown, _logsEnabledCheckBox, keyboardLayoutLabel]);

        tabs.TabPages.AddRange([apiKeysTab, generalTab, advancedTab]);

        // Ordre important : en WinForms, le DERNIER contrôle ajouté est ancré EN PREMIER.
        // tabs (Dock=Fill) doit donc être ajouté avant buttonPanel (Dock=Bottom), sinon
        // Fill réserve tout l'espace avant que Bottom ait pu prendre sa tranche → le
        // bandeau de boutons se retrouve écrasé à 0 pixel de hauteur, invisible.
        Controls.Add(tabs);
        Controls.Add(buttonPanel);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    /// <summary>Clés API telles que saisies (en clair, une par ligne, lignes vides ignorées) — à chiffrer par l'appelant.</summary>
    public IReadOnlyList<string> DecryptedApiKeys =>
        _apiKeysTextBox.Text
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

    public bool AutostartEnabled => _autostartCheckBox.Checked;

    /// <summary>Reconstruit un <see cref="AppConfig"/> à partir des champs (hors clés API, gérées à part — voir <see cref="DecryptedApiKeys"/>).</summary>
    public AppConfig ToConfig() => new()
    {
        TriggerWord = string.IsNullOrWhiteSpace(_triggerWordTextBox.Text) ? "donna" : _triggerWordTextBox.Text.Trim(),
        Model = string.IsNullOrWhiteSpace(_modelComboBox.Text) ? "gemini-2.5-flash" : _modelComboBox.Text.Trim(),
        PasteRestoreDelayMs = (int)_pasteDelayNumericUpDown.Value,
        LogsEnabled = _logsEnabledCheckBox.Checked,
    };
}
