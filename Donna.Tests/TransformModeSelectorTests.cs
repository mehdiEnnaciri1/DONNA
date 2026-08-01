using Donna.Core;
using Xunit;

namespace Donna.Tests;

public class TransformModeSelectorTests
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
    public void Source_tapee_donne_toujours_le_mode_TypedSource()
    {
        var m = Type("mon texte donna corrige  ");
        Assert.NotNull(m);

        var (mode, source) = TransformModeSelector.SelectMode(m!.Value, uiaFieldText: null);

        Assert.Equal(TransformMode.TypedSource, mode);
        Assert.Equal("mon texte", source);
    }

    [Fact]
    public void Source_tapee_ignore_la_lecture_uia_meme_si_disponible()
    {
        // Le mode 1 (source tapée) est prioritaire : peu importe ce qu'UI
        // Automation aurait pu lire par ailleurs.
        var m = Type("mon texte donna corrige  ");
        Assert.NotNull(m);

        var (mode, source) = TransformModeSelector.SelectMode(m!.Value, uiaFieldText: "n'importe quoi donna corrige  ");

        Assert.Equal(TransformMode.TypedSource, mode);
        Assert.Equal("mon texte", source);
    }

    [Fact]
    public void Sans_source_tapee_et_lecture_uia_exploitable_donne_UiaSource()
    {
        var m = Type("donna corrige  ");
        Assert.NotNull(m);

        var (mode, source) = TransformModeSelector.SelectMode(m!.Value, uiaFieldText: "texte collé plus tôt donna corrige  ");

        Assert.Equal(TransformMode.UiaSource, mode);
        Assert.Equal("texte collé plus tôt", source);
    }

    [Fact]
    public void Sans_source_tapee_et_lecture_uia_absente_donne_PureGeneration()
    {
        // Régression corrigée : une lecture UIA impossible (application non
        // supportée) ne doit jamais bloquer la génération pure.
        var m = Type("donna écris un haïku  ");
        Assert.NotNull(m);

        var (mode, source) = TransformModeSelector.SelectMode(m!.Value, uiaFieldText: null);

        Assert.Equal(TransformMode.PureGeneration, mode);
        Assert.Equal("", source);
    }

    [Fact]
    public void Sans_source_tapee_et_champ_vide_apres_retrait_de_la_formule_donne_PureGeneration()
    {
        var m = Type("donna écris un haïku  ");
        Assert.NotNull(m);

        // Le champ ne contenait QUE la formule elle-même (rien avant) : après
        // retrait, il ne reste rien → génération pure, pas une erreur.
        var (mode, source) = TransformModeSelector.SelectMode(m!.Value, uiaFieldText: "donna écris un haïku  ");

        Assert.Equal(TransformMode.PureGeneration, mode);
        Assert.Equal("", source);
    }

    [Fact]
    public void Sans_source_tapee_et_curseur_pas_en_fin_de_champ_donne_PureGeneration()
    {
        // Le texte lu ne se termine pas par la formule tapée (curseur ailleurs) :
        // TryExtractSourceFromFieldText échoue, donc on retombe sur le mode 3
        // plutôt que de deviner une source incorrecte.
        var m = Type("donna corrige  ");
        Assert.NotNull(m);

        var (mode, source) = TransformModeSelector.SelectMode(m!.Value, uiaFieldText: "donna corrige  et la suite du document");

        Assert.Equal(TransformMode.PureGeneration, mode);
        Assert.Equal("", source);
    }
}
