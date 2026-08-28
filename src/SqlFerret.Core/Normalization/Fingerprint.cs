using System.Security.Cryptography;
using System.Text;

namespace SqlFerret.Core.Normalization;

public static class Fingerprint
{
    public static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexStringLower(bytes);
    }
}
