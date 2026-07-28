using Donna.Ai;
using Xunit;

namespace Donna.Tests;

public class ResponseCleanerTests
{
    [Fact]
    public void Reponse_brute_inchangee()
    {
        Assert.Equal("Bonjour", ResponseCleaner.Clean("Bonjour"));
    }

    [Fact]
    public void Supprime_preambule_voici()
    {
        Assert.Equal("Bonjour, je souhaiterais savoir si le devis est prêt.",
            ResponseCleaner.Clean("Voici la version reformulée : Bonjour, je souhaiterais savoir si le devis est prêt."));
    }

    [Fact]
    public void Supprime_preambule_bien_sur_voici()
    {
        Assert.Equal("Bonjour", ResponseCleaner.Clean("Bien sûr, voici : Bonjour"));
    }

    [Fact]
    public void Supprime_preambule_anglais()
    {
        Assert.Equal("Hello", ResponseCleaner.Clean("Here is the result: Hello"));
    }

    [Fact]
    public void Supprime_guillemets_droits_encadrants()
    {
        Assert.Equal("Bonjour", ResponseCleaner.Clean("\"Bonjour\""));
    }

    [Fact]
    public void Supprime_guillemets_francais_encadrants()
    {
        Assert.Equal("Bonjour", ResponseCleaner.Clean("«Bonjour»"));
    }

    [Fact]
    public void Ne_supprime_pas_guillemets_internes_non_encadrants()
    {
        const string texte = "Il a dit \"bonjour\" à tout le monde";
        Assert.Equal(texte, ResponseCleaner.Clean(texte));
    }

    [Fact]
    public void Supprime_bloc_markdown_avec_langage()
    {
        const string reponse = "```csharp\nvar x = 1;\n```";
        Assert.Equal("var x = 1;", ResponseCleaner.Clean(reponse));
    }

    [Fact]
    public void Supprime_bloc_markdown_sans_langage()
    {
        const string reponse = "```\nBonjour\n```";
        Assert.Equal("Bonjour", ResponseCleaner.Clean(reponse));
    }

    [Fact]
    public void Combine_preambule_et_guillemets()
    {
        const string reponse = "Voici le résultat : \"Bonjour tout le monde\"";
        Assert.Equal("Bonjour tout le monde", ResponseCleaner.Clean(reponse));
    }

    [Fact]
    public void Chaine_vide_reste_vide()
    {
        Assert.Equal("", ResponseCleaner.Clean(""));
    }

    [Fact]
    public void Espaces_superflus_retires()
    {
        Assert.Equal("Bonjour", ResponseCleaner.Clean("   Bonjour   "));
    }
}
