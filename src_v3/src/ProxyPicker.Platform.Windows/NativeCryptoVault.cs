using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ProxyPicker.Platform.Windows
{
    /// <summary>
    /// 本地 DPAPI 当前用户加密（100% 兼容 v2.x static_proxy.dat）+ 跨机迁移 AES-256-CBC + HMAC-SHA256 认证加密。
    /// </summary>
    public static class NativeCryptoVault
    {
        static readonly byte[] LegacyDpapiEntropy = Encoding.UTF8.GetBytes("ProxyPicker.StaticProxy.DPAPI.v1");

        public static byte[] ProtectForCurrentUser(byte[] plainBytes)
        {
            return ProtectedData.Protect(plainBytes ?? new byte[0], LegacyDpapiEntropy, DataProtectionScope.CurrentUser);
        }

        public static byte[] UnprotectForCurrentUser(byte[] cipherBytes)
        {
            return ProtectedData.Unprotect(cipherBytes, LegacyDpapiEntropy, DataProtectionScope.CurrentUser);
        }

        /// <summary>跨机便携导出用认证加密（PBKDF2-SHA256 100k 轮 + AES-256-CBC + Encrypt-then-MAC HMAC-SHA256）。</summary>
        public static string EncryptPortable(string plainText, string passphrase)
        {
            if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("导出加密口令不能为空");
            byte[] salt = new byte[16];
            byte[] iv = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
                rng.GetBytes(iv);
            }

            byte[] keyMat;
            using (var kdf = new Rfc2898DeriveBytes(passphrase, salt, 100000, HashAlgorithmName.SHA256))
                keyMat = kdf.GetBytes(64); // 32B AES + 32B HMAC

            byte[] aesKey = new byte[32];
            byte[] macKey = new byte[32];
            Buffer.BlockCopy(keyMat, 0, aesKey, 0, 32);
            Buffer.BlockCopy(keyMat, 32, macKey, 0, 32);

            byte[] cipher;
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.IV = iv;
                using (var enc = aes.CreateEncryptor())
                {
                    byte[] plain = Encoding.UTF8.GetBytes(plainText ?? "");
                    cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
                }
            }

            byte[] payload = new byte[1 + 16 + 16 + cipher.Length];
            payload[0] = 1; // version
            Buffer.BlockCopy(salt, 0, payload, 1, 16);
            Buffer.BlockCopy(iv, 0, payload, 17, 16);
            Buffer.BlockCopy(cipher, 0, payload, 33, cipher.Length);

            byte[] mac;
            using (var hmac = new HMACSHA256(macKey))
                mac = hmac.ComputeHash(payload);

            byte[] finalBuf = new byte[payload.Length + mac.Length];
            Buffer.BlockCopy(payload, 0, finalBuf, 0, payload.Length);
            Buffer.BlockCopy(mac, 0, finalBuf, payload.Length, mac.Length);
            return Convert.ToBase64String(finalBuf);
        }

        public static string DecryptPortable(string base64Envelope, string passphrase)
        {
            if (string.IsNullOrWhiteSpace(base64Envelope) || string.IsNullOrEmpty(passphrase))
                throw new CryptographicException("密文或口令为空");

            byte[] raw = Convert.FromBase64String(base64Envelope.Trim());
            if (raw.Length < 1 + 16 + 16 + 16 + 32 || raw[0] != 1)
                throw new CryptographicException("加密包格式或版本不受支持");

            int payloadLen = raw.Length - 32;
            byte[] payload = new byte[payloadLen];
            byte[] expectedMac = new byte[32];
            Buffer.BlockCopy(raw, 0, payload, 0, payloadLen);
            Buffer.BlockCopy(raw, payloadLen, expectedMac, 0, 32);

            byte[] salt = new byte[16];
            byte[] iv = new byte[16];
            Buffer.BlockCopy(payload, 1, salt, 0, 16);
            Buffer.BlockCopy(payload, 17, iv, 0, 16);

            byte[] keyMat;
            using (var kdf = new Rfc2898DeriveBytes(passphrase, salt, 100000, HashAlgorithmName.SHA256))
                keyMat = kdf.GetBytes(64);
            byte[] aesKey = new byte[32];
            byte[] macKey = new byte[32];
            Buffer.BlockCopy(keyMat, 0, aesKey, 0, 32);
            Buffer.BlockCopy(keyMat, 32, macKey, 0, 32);

            byte[] actualMac;
            using (var hmac = new HMACSHA256(macKey))
                actualMac = hmac.ComputeHash(payload);
            if (!FixedTimeEquals(expectedMac, actualMac))
                throw new CryptographicException("口令错误或配置包已被篡改");

            byte[] cipher = new byte[payloadLen - 33];
            Buffer.BlockCopy(payload, 33, cipher, 0, cipher.Length);
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = aesKey;
                aes.IV = iv;
                using (var dec = aes.CreateDecryptor())
                {
                    byte[] plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                    return Encoding.UTF8.GetString(plain);
                }
            }
        }

        static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
