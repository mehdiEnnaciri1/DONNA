using Donna.Config;
using Xunit;

namespace Donna.Tests;

public class KeyRingTests
{
    [Fact]
    public void Cle_courante_est_la_premiere_au_depart()
    {
        var ring = new KeyRing(["clé-A", "clé-B", "clé-C"]);
        Assert.Equal("clé-A", ring.CurrentKey);
        Assert.False(ring.IsExhausted);
    }

    [Fact]
    public void Liste_vide_leve_une_exception()
    {
        Assert.Throws<ArgumentException>(() => new KeyRing([]));
    }

    [Fact]
    public void Rotation_sur_quota_passe_a_la_cle_suivante()
    {
        var ring = new KeyRing(["clé-A", "clé-B"]);
        var suivante = ring.MarkCurrentAsFailed();

        Assert.Equal("clé-B", suivante);
        Assert.Equal("clé-B", ring.CurrentKey);
    }

    [Fact]
    public void Rotation_boucle_sur_plusieurs_cles_dans_l_ordre()
    {
        var ring = new KeyRing(["clé-A", "clé-B", "clé-C"]);

        Assert.Equal("clé-B", ring.MarkCurrentAsFailed());
        Assert.Equal("clé-C", ring.MarkCurrentAsFailed());
    }

    [Fact]
    public void Une_seule_cle_epuisee_rend_le_trousseau_epuise()
    {
        var ring = new KeyRing(["clé-unique"]);

        var suivante = ring.MarkCurrentAsFailed();

        Assert.Null(suivante);
        Assert.Null(ring.CurrentKey);
        Assert.True(ring.IsExhausted);
    }

    [Fact]
    public void Toutes_les_cles_epuisees_rend_null()
    {
        var ring = new KeyRing(["clé-A", "clé-B"]);

        ring.MarkCurrentAsFailed(); // A épuisée → passe à B
        var apresB = ring.MarkCurrentAsFailed(); // B épuisée → plus rien

        Assert.Null(apresB);
        Assert.True(ring.IsExhausted);
    }

    [Fact]
    public void Marquer_une_cle_deja_epuisee_ne_casse_pas_la_rotation()
    {
        var ring = new KeyRing(["clé-A", "clé-B", "clé-C"]);

        ring.MarkCurrentAsFailed(); // A épuisée → B courante
        ring.MarkCurrentAsFailed(); // B épuisée → C courante
        var apresC = ring.MarkCurrentAsFailed(); // C épuisée → plus rien

        Assert.Null(apresC);
        Assert.True(ring.IsExhausted);
    }
}
