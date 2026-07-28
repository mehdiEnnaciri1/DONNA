using Donna.Config;
using Xunit;

namespace Donna.Tests;

public class DpapiSecretTests
{
    [Fact]
    public void Protect_puis_Unprotect_redonne_le_texte_original()
    {
        const string secret = "AIzaSyD-exemple-de-cle-avec-accents-éàü";

        string encrypted = DpapiSecret.Protect(secret);
        string decrypted = DpapiSecret.Unprotect(encrypted);

        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void Protect_ne_renvoie_pas_le_texte_en_clair()
    {
        const string secret = "AIzaSyD-ne-doit-jamais-apparaitre-en-clair";

        string encrypted = DpapiSecret.Protect(secret);

        Assert.DoesNotContain(secret, encrypted);
    }

    [Fact]
    public void Deux_chiffrements_du_meme_secret_donnent_des_resultats_differents()
    {
        // DPAPI ajoute du sel : deux appels sur le même texte ne doivent pas
        // produire le même blob (sinon on pourrait comparer des clés chiffrées
        // entre elles pour en déduire qu'elles sont identiques).
        const string secret = "AIzaSyD-meme-cle-deux-fois";

        string first = DpapiSecret.Protect(secret);
        string second = DpapiSecret.Protect(secret);

        Assert.NotEqual(first, second);
        Assert.Equal(secret, DpapiSecret.Unprotect(first));
        Assert.Equal(secret, DpapiSecret.Unprotect(second));
    }
}
