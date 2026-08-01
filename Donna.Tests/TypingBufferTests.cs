using Donna.Core;
using Xunit;

namespace Donna.Tests;

public class TypingBufferTests
{
    // Simule une saisie caractère par caractère et renvoie la dernière détection.
    private static TriggerMatch? Type(string text, string trigger = "donna")
    {
        var buf = new TypingBuffer(trigger);
        TriggerMatch? last = null;
        foreach (var ch in text)
            last = buf.Append(ch.ToString());
        return last;
    }

    [Fact]
    public void Exemple_Outlook_source_et_prompt()
    {
        const string frappe = "Bonjour je voulais savoir si le devis est pret donna rends ça formel  ";
        var m = Type(frappe);

        Assert.NotNull(m);
        Assert.Equal("Bonjour je voulais savoir si le devis est pret", m!.Value.Source);
        Assert.Equal("rends ça formel", m.Value.Prompt);
        Assert.Equal(frappe.Length, m.Value.CharsToDelete); // on efface toute la formule
    }

    [Fact]
    public void Prompt_seul_sans_source()
    {
        var m = Type("donna écris un haïku sur la mer  ");
        Assert.NotNull(m);
        Assert.Equal("", m!.Value.Source);
        Assert.Equal("écris un haïku sur la mer", m.Value.Prompt);
    }

    [Fact]
    public void Source_seule_sans_prompt()
    {
        var m = Type("réunion demain 14h donna  ");
        Assert.NotNull(m);
        Assert.Equal("réunion demain 14h", m!.Value.Source);
        Assert.Equal("", m.Value.Prompt);
    }

    [Fact]
    public void Un_seul_espace_ne_declenche_pas()
    {
        var m = Type("texte donna reformule "); // un seul espace final
        Assert.Null(m);
    }

    [Fact]
    public void Le_mot_madonna_ne_declenche_pas()
    {
        var m = Type("j'écoute madonna  ");
        Assert.Null(m);
    }

    [Fact]
    public void Casse_du_declencheur_ignoree()
    {
        var m = Type("un texte DONNA traduis en anglais  ");
        Assert.NotNull(m);
        Assert.Equal("un texte", m!.Value.Source);
        Assert.Equal("traduis en anglais", m.Value.Prompt);
    }

    [Fact]
    public void Backspace_corrige_le_buffer()
    {
        var buf = new TypingBuffer();
        foreach (var ch in "textx") buf.Append(ch.ToString());
        buf.Backspace();      // enlève le « x »
        buf.Append("e");      // → « texte »
        Assert.Equal("texte", buf.Current);
    }

    [Fact]
    public void Reset_vide_le_buffer()
    {
        var buf = new TypingBuffer();
        buf.Append("abc");
        buf.Reset();
        Assert.Equal(0, buf.Length);
    }

    [Fact]
    public void TriggerLength_couvre_juste_declencheur_prompt_et_espaces_quand_source_presente()
    {
        const string frappe = "Bonjour je voulais savoir si le devis est pret donna rends ça formel  ";
        var m = Type(frappe);

        Assert.NotNull(m);
        const string queueAttendue = "donna rends ça formel  "; // déclencheur + prompt + 2 espaces, sans la source
        Assert.Equal(queueAttendue.Length, m!.Value.TriggerLength);
        Assert.Equal(frappe.Length, m.Value.CharsToDelete); // CharsToDelete, lui, couvre tout
    }

    [Fact]
    public void TriggerLength_egale_CharsToDelete_quand_pas_de_source_tapee()
    {
        // Sans source tapée, le déclencheur est en tout début de buffer : les deux
        // longueurs coïncident (DonnaContext bascule alors sur le repli par sélection).
        var m = Type("donna écris un haïku sur la mer  ");

        Assert.NotNull(m);
        Assert.Equal(0, m!.Value.Source.Length);
        Assert.Equal(m.Value.CharsToDelete, m.Value.TriggerLength);
    }

    [Fact]
    public void TriggerLength_couvre_juste_declencheur_et_espaces_quand_pas_de_prompt()
    {
        const string frappe = "réunion demain 14h donna  ";
        var m = Type(frappe);

        Assert.NotNull(m);
        const string queueAttendue = "donna  "; // déclencheur + 2 espaces, prompt vide
        Assert.Equal(queueAttendue.Length, m!.Value.TriggerLength);
    }

    [Fact]
    public void TypedSuffix_correspond_exactement_a_la_queue_tapee()
    {
        const string frappe = "donna corrige  ";
        var m = Type(frappe);

        Assert.NotNull(m);
        Assert.Equal(frappe, m!.Value.TypedSuffix);
        Assert.Equal(m.Value.TypedSuffix.Length, m.Value.TriggerLength);
    }

    [Fact]
    public void TryExtractSourceFromFieldText_reussit_quand_le_champ_se_termine_par_la_formule()
    {
        // Cas nominal : le curseur était en fin de champ au moment du déclenchement,
        // donc le texte lu (ex. via UI Automation) se termine bien par ce qui a été tapé.
        var m = Type("donna corrige  ");
        Assert.NotNull(m);

        bool ok = m!.Value.TryExtractSourceFromFieldText("texte collé plus tôt donna corrige  ", out string source);

        Assert.True(ok);
        Assert.Equal("texte collé plus tôt", source);
    }

    [Fact]
    public void TryExtractSourceFromFieldText_echoue_si_le_curseur_n_etait_pas_en_fin_de_champ()
    {
        // Bug réel évité : si le curseur était au milieu du document (ex. clic dans un
        // texte déjà présent), le champ ne se termine PAS par la formule tapée — une
        // troncature aveugle par longueur couperait du vrai contenu et laisserait la
        // formule dans la source. On doit détecter ce cas et refuser, pas deviner.
        var m = Type("donna corrige  ");
        Assert.NotNull(m);

        bool ok = m!.Value.TryExtractSourceFromFieldText("donna corrige  et voici la suite du document", out string source);

        Assert.False(ok);
        Assert.Equal("", source);
    }

    [Fact]
    public void TryExtractSourceFromFieldText_echoue_si_le_champ_est_plus_court_que_la_formule()
    {
        var m = Type("donna corrige tout ce texte  ");
        Assert.NotNull(m);

        bool ok = m!.Value.TryExtractSourceFromFieldText("court", out string source);

        Assert.False(ok);
        Assert.Equal("", source);
    }

    [Fact]
    public void Une_formule_traitee_ne_contamine_pas_la_formule_suivante()
    {
        // Bug réel : sans reset après un match, le texte tapé ensuite s'accumule
        // par-dessus l'ancienne formule déjà traitée.
        var buf = new TypingBuffer();
        foreach (var ch in "ancien texte donna vieux prompt  ")
            buf.Append(ch.ToString());

        TriggerMatch? second = null;
        foreach (var ch in "nouveau texte donna nouveau prompt  ")
            second = buf.Append(ch.ToString());

        Assert.NotNull(second);
        Assert.Equal("nouveau texte", second!.Value.Source);
        Assert.Equal("nouveau prompt", second.Value.Prompt);
    }
}
