using Donna.Input;
using Xunit;

namespace Donna.Tests;

public class VerifiedFieldWriterTests
{
    [Fact]
    public void CountBackspacesNeeded_sans_saut_de_ligne_egale_la_longueur()
    {
        Assert.Equal(5, VerifiedFieldWriter.CountBackspacesNeeded("hello"));
    }

    [Fact]
    public void CountBackspacesNeeded_traite_CRLF_comme_un_seul_caractere()
    {
        // "a\r\nb" fait 4 caractères bruts, mais un seul Backspace efface le
        // saut de ligne dans la plupart des contrôles de texte : 3 attendus.
        Assert.Equal(3, VerifiedFieldWriter.CountBackspacesNeeded("a\r\nb"));
    }

    [Fact]
    public void CountBackspacesNeeded_traite_LF_seul_comme_un_caractere()
    {
        Assert.Equal(3, VerifiedFieldWriter.CountBackspacesNeeded("a\nb"));
    }

    [Fact]
    public void CountBackspacesNeeded_chaine_vide_donne_zero()
    {
        Assert.Equal(0, VerifiedFieldWriter.CountBackspacesNeeded(""));
    }

    [Fact]
    public void CountBackspacesNeeded_plusieurs_lignes_CRLF()
    {
        const string texte = "ligne1\r\nligne2\r\nligne3"; // 3 lignes, 2 sauts CRLF
        // 6 + 1 + 6 + 1 + 6 = 20 après normalisation (chaque \r\n compte pour 1)
        Assert.Equal(20, VerifiedFieldWriter.CountBackspacesNeeded(texte));
    }
}
