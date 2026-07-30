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
