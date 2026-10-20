using System;
using System.Collections.Generic;
using RASv2.Core;

namespace RASv2.Tests
{
    /// <summary>Режимы модулей ({Модули} в ribbon.txt — ModuleSwitches) и предохранитель (ErrorFuse).</summary>
    internal static class ModuleSwitchTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static Dictionary<string, ModuleMode> Parse(string text, List<string> warnings = null) =>
            ModuleSwitches.Parse(text.Split('\n'), w => warnings?.Add(w));

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("режимы модулей по-русски, без учёта регистра и лишних пробелов", () =>
            {
                var s = Parse("# комментарий\nModelHistory = Работает\nKj=выключен\r\nOv =  только   тест  # пробелы\nSleeves = только тест\nBroken = не загружать");
                Check(ModuleSwitches.ModeOf(s, "ModelHistory") == ModuleMode.Works, "работает");
                Check(ModuleSwitches.ModeOf(s, "kj") == ModuleMode.Off, "выключен");
                Check(ModuleSwitches.ModeOf(s, "Ov") == ModuleMode.TestOnly, "лишние пробелы между словами");
                Check(ModuleSwitches.ModeOf(s, "Sleeves") == ModuleMode.TestOnly, "только тест");
                Check(ModuleSwitches.ModeOf(s, "Broken") == ModuleMode.NoLoad, "не загружать");
            });
            yield return Case("модуля нет в файле или файла нет — модуль работает", () =>
            {
                Check(ModuleSwitches.ModeOf(Parse("Kj = выключен"), "ModelHistory") == ModuleMode.Works, "нет в файле");
                Check(ModuleSwitches.ModeOf(null, "ModelHistory") == ModuleMode.Works, "нет файла");
            });
            yield return Case("непонятная строка пропускается — модуль работает, причина в журнале", () =>
            {
                var warnings = new List<string>();
                var s = Parse("ModelHistory = выкл\nбез знака равенства\n = выключен\nKj = выключен", warnings);
                Check(ModuleSwitches.ModeOf(s, "ModelHistory") == ModuleMode.Works, "опечатка выключила модуль");
                Check(ModuleSwitches.ModeOf(s, "Kj") == ModuleMode.Off, "правильная строка не применилась");
                Check(warnings.Count == 3, "предупреждений: " + warnings.Count);
            });
            yield return Case("модуль указан дважды — действует последняя строка", () =>
            {
                Check(ModuleSwitches.ModeOf(Parse("Kj = выключен\nKj = работает"), "Kj") == ModuleMode.Works, "первая строка");
            });
            yield return Case("«только тест» — у тестовой группы и у разработчика", () =>
            {
                Check(ModuleSwitches.IsTestChannel("test") && ModuleSwitches.IsTestChannel("dev"), "тест / разработка");
                Check(!ModuleSwitches.IsTestChannel("all") && !ModuleSwitches.IsTestChannel(null), "все");
            });
            yield return Case("предохранитель: единичные ошибки модуль не выключают", () =>
            {
                var fuse = new ErrorFuse();
                var t = new DateTime(2026, 10, 6, 10, 0, 0);
                // по ошибке раз в 10 секунд — в минуту всего 6
                for (var i = 0; i < 100; i++) Check(!fuse.Record("Kj", t.AddSeconds(10 * i)), "выключен на ошибке " + (i + 1));
                Check(!fuse.IsTripped("Kj"), "выключен");
            });
            yield return Case("предохранитель: много ошибок за минуту — модуль выключен один раз, другие работают", () =>
            {
                var fuse = new ErrorFuse();
                var t = new DateTime(2026, 10, 6, 10, 0, 0);
                var trips = 0;
                for (var i = 0; i < ErrorFuse.Limit * 2; i++)
                    if (fuse.Record("Kj", t.AddMilliseconds(100 * i))) trips++;
                Check(trips == 1 && fuse.IsTripped("kj"), "срабатываний: " + trips);
                Check(!fuse.IsTripped("ModelHistory") && !fuse.Record("ModelHistory", t), "задет другой модуль");
            });
        }
    }
}
