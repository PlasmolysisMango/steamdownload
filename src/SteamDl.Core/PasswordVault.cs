using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SteamDl.Core
{
    static class PasswordVault
    {
        static readonly string KeyPath = Path.Combine(AppPaths.DataDir, "password.key");

        public static string Protect(string password)
        {
            if (string.IsNullOrEmpty(password)) return null;
            var key = LoadOrCreateKey();
            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            var plain = Encoding.UTF8.GetBytes(password);
            var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
            var payload = new byte[aes.IV.Length + cipher.Length];
            Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
            Buffer.BlockCopy(cipher, 0, payload, aes.IV.Length, cipher.Length);
            return "v1:" + Convert.ToBase64String(payload);
        }

        public static string Unprotect(string protectedPassword)
        {
            if (string.IsNullOrWhiteSpace(protectedPassword)) return null;
            if (!protectedPassword.StartsWith("v1:", StringComparison.Ordinal)) return null;
            var payload = Convert.FromBase64String(protectedPassword[3..]);
            if (payload.Length <= 16) return null;
            var iv = new byte[16];
            var cipher = new byte[payload.Length - 16];
            Buffer.BlockCopy(payload, 0, iv, 0, iv.Length);
            Buffer.BlockCopy(payload, iv.Length, cipher, 0, cipher.Length);
            using var aes = Aes.Create();
            aes.Key = LoadOrCreateKey();
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
            return Encoding.UTF8.GetString(plain);
        }

        static byte[] LoadOrCreateKey()
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            if (File.Exists(KeyPath)) return Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(KeyPath, Convert.ToBase64String(key));
            return key;
        }
    }
}
