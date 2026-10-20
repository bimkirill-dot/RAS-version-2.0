using System;
using System.Security.Cryptography;

namespace RASv2.ClashAnalysis.Site.Logic
{
    /// <summary>
    /// Пароли учётных записей сайта (DECISIONS.md модуля, «Вход на сайт»): хранится не пароль, а его отпечаток PBKDF2-SHA256
    /// со своей солью у каждого — «pbkdf2-sha256$итераций$соль$отпечаток». Без сторонних библиотек; проверяется тестами.
    /// </summary>
    public static class Passwords
    {
        public const int MinLength = 6;
        private const string Scheme = "pbkdf2-sha256";
        private const int Iterations = 120_000, SaltSize = 16, HashSize = 32;

        /// <summary>Что не так с паролем; годится — null.</summary>
        public static string Problem(string password) =>
            string.IsNullOrEmpty(password) ? "Введите пароль."
            : password.Length < MinLength ? $"Пароль — не короче {MinLength} знаков."
            : password.Trim().Length == 0 ? "Пароль из одних пробелов не годится."
            : null;

        public static string Hash(string password)
        {
            var salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            return Scheme + "$" + Iterations + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(Derive(password, salt, Iterations));
        }

        public static bool Verify(string password, string stored)
        {
            if (password == null || string.IsNullOrEmpty(stored)) return false;
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations <= 0) return false;
            byte[] salt, expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                return false;
            }
            return SameBytes(Derive(password, salt, iterations), expected);
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(HashSize);
        }

        /// <summary>Сравнение за одно и то же время, совпали ли байты раньше или позже: по времени ответа пароль не подобрать.</summary>
        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
