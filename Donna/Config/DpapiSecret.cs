using System.Security.Cryptography;
using System.Text;

namespace Donna.Config;

/// <summary>
/// Chiffre/déchiffre des secrets (clés API) via DPAPI, liés au compte Windows
/// courant : illisibles sur une autre machine ou par un autre utilisateur,
/// même avec un accès direct à config.json. Voir ARCHITECTURE.md §8.
/// </summary>
public static class DpapiSecret
{
    // Entropie additionnelle : durcit un peu contre un secret DPAPI générique
    // partagé par erreur avec une autre application du même utilisateur.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Donna.Config.DpapiSecret.v1");

    /// <summary>Chiffre un texte en clair → chaîne base64, à écrire telle quelle dans config.json.</summary>
    public static string Protect(string plainText)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }

    /// <summary>Déchiffre une valeur produite par <see cref="Protect"/>.</summary>
    public static string Unprotect(string encryptedBase64)
    {
        byte[] encryptedBytes = Convert.FromBase64String(encryptedBase64);
        byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
