
using System;
using System.Security.Cryptography;
using System.Text;

namespace Sleptify.Shell.Interop
{
    public static class SpotifyPkce
    {
        public static string CreateCodeVerifier(int length = 64)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";
            using var rng = RandomNumberGenerator.Create();
            var bytes = new byte[length];
            rng.GetBytes(bytes);
            var sb = new StringBuilder(length);
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        public static string CreateCodeChallenge(string verifier)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            return Convert.ToBase64String(hash).Replace("+","-").Replace("/","_").Replace("=","");
        }
    }
}
