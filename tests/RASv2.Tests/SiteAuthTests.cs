using System;
using System.Collections.Generic;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis, сайт: пароли учётных записей (Passwords).</summary>
    internal static class SiteAuthTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("пароль: хранится отпечаток, верный — подходит, неверный — нет", () =>
            {
                var stored = Passwords.Hash("Секрет_2026");
                Check(!stored.Contains("Секрет_2026") && stored.StartsWith("pbkdf2-sha256$", StringComparison.Ordinal), "пароля в записи нет");
                Check(Passwords.Verify("Секрет_2026", stored), "верный пароль");
                Check(!Passwords.Verify("секрет_2026", stored) && !Passwords.Verify("", stored) && !Passwords.Verify(null, stored), "неверный, пустой, нет пароля");
            });
            yield return Case("пароль: у каждого своя соль — одинаковые пароли не видны по записи", () =>
            {
                Check(Passwords.Hash("одинаковый") != Passwords.Hash("одинаковый"), "две записи одного пароля разные");
            });
            yield return Case("пароль: испорченная запись — не подходит, без ошибки", () =>
            {
                Check(!Passwords.Verify("x", "") && !Passwords.Verify("x", "abc") && !Passwords.Verify("x", "pbkdf2-sha256$0$AA==$AA==")
                      && !Passwords.Verify("x", "pbkdf2-sha256$100$не base64$AA=="), "испорченные записи");
            });
            yield return Case("пароль: не короче 6 знаков, не из пробелов", () =>
            {
                Check(Passwords.Problem("12345") != null && Passwords.Problem("      ") != null && Passwords.Problem(null) != null, "плохие");
                Check(Passwords.Problem("123456") == null, "годный");
            });
        }
    }
}
