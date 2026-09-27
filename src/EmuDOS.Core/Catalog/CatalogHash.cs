using System.Security.Cryptography;
using System.Text;

namespace EmuDOS.Core.Catalog;

/// <summary>
/// The catalog stores every identifying string — titles, telltale file names, launch programs — only
/// as a digest, so the published catalog (a release asset and the copy embedded in the app) carries
/// no readable names. Lookups hash the imported content's names the same way and compare digests;
/// matching is exact, so hashing loses nothing the normalised comparison did not already give up.
/// </summary>
public static class CatalogHash
{
    // Domain prefix: a catalog key never equals the plain SHA-256 of the same string.
    private const string Domain = "emudos-catalog/1:";

    /// <summary>The key for an already-normalised string (32 lowercase hex characters, 128 bits).
    /// Empty input has no key.</summary>
    public static string Of(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
            return string.Empty;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Domain + normalized));
        return Convert.ToHexStringLower(digest, 0, 16);
    }
}
