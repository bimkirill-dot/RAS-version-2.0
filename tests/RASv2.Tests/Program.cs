using System;
using System.Collections.Generic;
using System.IO;
using RASv2.Loader;

namespace RASv2.Tests
{
    /// <summary>
    /// Проверки логики без Revit (PRINCIPLES.md, раздел 11). Запускаются при каждой сборке (tools\Release.ps1);
    /// код возврата не 0 — сборка не выпускается. Без сторонних библиотек: в Revit 2023 они конфликтуют.
    /// </summary>
    internal static class Program
    {
        private static int _failed;

        private static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Run("Загрузчик: выбор сборки", LoaderTests.All);
            Run("Ядро: раскладка ленты", RibbonTests.All);
            Run("Ядро: выключение модулей и предохранитель", ModuleSwitchTests.All);
            Run("RAS-Clash Analysis: отчёты Navisworks", ClashReportTests.All);
            Run("RAS-Clash Analysis: новый отчёт против известного", ClashMergeTests.All);
#if REVIT2026
            Run("RAS-Model History: фильтр", ModelHistoryTests.Filter);
            Run("RAS-Model History: пары «правка + отмена»", ModelHistoryTests.Undo);
            Run("RAS-Model History: журнал", ModelHistoryTests.Journal);
            Run("RAS-Model History: сотрудник за автором", ModelHistoryTests.Employees);
            Run("RAS-Model History: перенос личных папок", ModelHistoryTests.Folders);
            Run("RAS-Model History: удалённые — «вместе с»", ModelHistoryElementTests.Deleted);
            Run("RAS-Model History: «нет сведений» — из журнала", ModelHistoryElementTests.Unknown);
            Run("RAS-Model History: кэш сведений об элементах", ModelHistoryElementTests.Cache);
            Run("RAS-Model History: «Модель запомнена»", ModelHistoryElementTests.Warmup);
#endif
            Console.WriteLine(_failed == 0 ? "Все проверки прошли." : "Не прошло проверок: " + _failed);
            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string title, Func<IEnumerable<KeyValuePair<string, Action>>> cases)
        {
            Console.WriteLine(title);
            foreach (var c in cases())
            {
                try
                {
                    c.Value();
                    Console.WriteLine("  ✓ " + c.Key);
                }
                catch (Exception ex)
                {
                    _failed++;
                    Console.WriteLine("  ✗ " + c.Key + ": " + ex.Message);
                }
            }
        }

        internal static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception(what);
        }
    }

    internal static class LoaderTests
    {
        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("сборка канала копируется и запускается из локальной копии", () =>
            {
                using (var env = new Env())
                {
                    env.Publish("2026.10.05-100000", "all");
                    var c = BuildResolver.Resolve(env.Settings(), null);
                    Program.Check(c.BuildId == "2026.10.05-100000" && c.Note == null, "выбрана " + c.BuildId + " (" + c.Note + ")");
                    Program.Check(c.Dir.StartsWith(env.Cache) && BuildResolver.IsReady(c.Dir), "копия не в локальной папке или не готова");
                }
            });
            yield return Case("готовая копия запускается и без сервера", () =>
            {
                using (var env = new Env())
                {
                    env.Publish("2026.10.05-100000", "all");
                    var first = BuildResolver.Resolve(env.Settings(), null);
                    BuildResolver.MarkGood(env.Cache, first.BuildId);
                    var offline = env.Settings();
                    offline.ReleasesRoot = Path.Combine(env.Root, "нет-такой-папки");
                    var c = BuildResolver.Resolve(offline, null);
                    Program.Check(c.BuildId == "2026.10.05-100000" && c.Note != null, "без сервера: " + c.BuildId + " (" + c.Note + ")");
                }
            });
            yield return Case("новая сборка заменяет прежнюю после выпуска", () =>
            {
                using (var env = new Env())
                {
                    env.Publish("2026.10.05-100000", "all");
                    BuildResolver.Resolve(env.Settings(), null);
                    env.Publish("2026.10.05-110000", "all");
                    var c = BuildResolver.Resolve(env.Settings(), null);
                    Program.Check(c.BuildId == "2026.10.05-110000", "выбрана " + c.BuildId);
                }
            });
            yield return Case("не запустившаяся сборка пропускается — берётся прежняя", () =>
            {
                using (var env = new Env())
                {
                    env.Publish("2026.10.05-100000", "all");
                    var good = BuildResolver.Resolve(env.Settings(), null);
                    BuildResolver.MarkGood(env.Cache, good.BuildId);
                    env.Publish("2026.10.05-110000", "all");
                    var bad = BuildResolver.Resolve(env.Settings(), null);
                    BuildResolver.MarkBad(bad.Dir);
                    var c = BuildResolver.Resolve(env.Settings(), null);
                    Program.Check(c.BuildId == "2026.10.05-100000" && c.Note != null, "выбрана " + c.BuildId + " (" + c.Note + ")");
                }
            });
            yield return Case("тестовая группа получает сборку канала «тест»", () =>
            {
                using (var env = new Env())
                {
                    env.Publish("2026.10.05-100000", "all");
                    env.Publish("2026.10.05-110000", "test");
                    File.WriteAllLines(env.Testers, new[] { "# тестовая группа", "Petrov", "TestUser" });
                    var tester = BuildResolver.Resolve(env.Settings(), null);
                    Program.Check(tester.BuildId == "2026.10.05-110000" && tester.Channel == "test", "тестировщику: " + tester.BuildId);
                    var other = env.Settings();
                    other.UserName = "Ivanov";
                    other.CacheRoot = Path.Combine(env.Root, "cache-ivanov");
                    var c = BuildResolver.Resolve(other, null);
                    Program.Check(c.BuildId == "2026.10.05-100000" && c.Channel == "all", "остальным: " + c.BuildId);
                }
            });
            yield return Case("у тестовой группы нет своей сборки — та же, что у всех", () =>
            {
                using (var env = new Env())
                {
                    env.Publish("2026.10.05-100000", "all");
                    File.WriteAllLines(env.Testers, new[] { "TestUser" });
                    var c = BuildResolver.Resolve(env.Settings(), null);
                    Program.Check(c.BuildId == "2026.10.05-100000" && c.Channel == "all", "выбрана " + c.BuildId + ", канал " + c.Channel);
                }
            });
            yield return Case("нет ни выпусков, ни копий — запускать нечего", () =>
            {
                using (var env = new Env())
                {
                    var c = BuildResolver.Resolve(env.Settings(), null);
                    Program.Check(c.Dir == null && c.Note != null, "выбрано: " + c.Dir);
                }
            });
            yield return Case("недокопированная сборка (без ядра) не считается готовой", () =>
            {
                using (var env = new Env())
                {
                    var dir = Path.Combine(env.Releases, "2026", "2026.10.05-100000");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "RASv2.ModelHistory.dll"), "x");
                    File.WriteAllText(Path.Combine(env.Releases, "2026", "all.txt"), "2026.10.05-100000");
                    var c = BuildResolver.Resolve(env.Settings(), null);
                    Program.Check(c.Dir == null, "запущена неполная сборка");
                }
            });
            yield return Case("старые копии удаляются, текущая и последняя запустившаяся остаются", () =>
            {
                using (var env = new Env())
                {
                    string last = null;
                    for (var h = 10; h <= 16; h++)
                    {
                        env.Publish("2026.10.05-" + h + "0000", "all");
                        last = BuildResolver.Resolve(env.Settings(), null).BuildId;
                    }
                    BuildResolver.MarkGood(env.Cache, "2026.10.05-100000");
                    BuildResolver.Cleanup(env.Cache, last, keep: 2);
                    var left = Directory.GetDirectories(env.Cache).Length;
                    Program.Check(left == 4, "осталось копий: " + left);
                    Program.Check(Directory.Exists(Path.Combine(env.Cache, "2026.10.05-100000")), "удалена последняя запустившаяся");
                    Program.Check(Directory.Exists(Path.Combine(env.Cache, last)), "удалена текущая");
                }
            });
            yield return Case("указатель с путём вместо имени сборки не принимается", () =>
            {
                using (var env = new Env())
                {
                    Directory.CreateDirectory(Path.Combine(env.Releases, "2026"));
                    File.WriteAllText(Path.Combine(env.Releases, "2026", "all.txt"), @"..\..\чужая");
                    Program.Check(BuildResolver.ReadPointer(Path.Combine(env.Releases, "2026"), "all") == null, "путь принят");
                }
            });
        }

        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        /// <summary>Временные «сервер» и «компьютер пользователя».</summary>
        private sealed class Env : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "rasv2-tests-" + Guid.NewGuid().ToString("N"));
            public string Releases => Path.Combine(Root, "Releases");
            public string Cache => Path.Combine(Root, "cache");
            public string Testers => Path.Combine(Root, "test-users.txt");

            public LoaderSettings Settings() => new LoaderSettings
            {
                ReleasesRoot = Releases,
                TestersFile = Testers,
                CacheRoot = Cache,
                Year = 2026,
                UserName = "TestUser",
                TimeoutMs = 10000
            };

            /// <summary>Выпустить сборку: папка с «ядром» и указатель канала.</summary>
            public void Publish(string id, string channel)
            {
                var dir = Path.Combine(Releases, "2026", id);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, BuildResolver.EntryAssembly), "ядро " + id);
                File.WriteAllText(Path.Combine(dir, "RASv2.ModelHistory.dll"), "модуль " + id);
                File.WriteAllText(Path.Combine(Releases, "2026", channel + ".txt"), id);
            }

            public void Dispose()
            {
                try { Directory.Delete(Root, true); }
                catch (Exception) { /* временная папка — не страшно */ }
            }
        }
    }
}
