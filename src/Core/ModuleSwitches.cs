using System;
using System.Collections.Generic;

namespace RASv2.Core
{
    /// <summary>Как модуль работает у всех — блок {Модули} файла ribbon.txt в сборке.</summary>
    internal enum ModuleMode
    {
        /// <summary>Как обычно (и если модуля в файле нет).</summary>
        Works,
        /// <summary>Только у тестовой группы (канал «тест»; у разработчика — тоже).</summary>
        TestOnly,
        /// <summary>Сборка загружена, модуль не запускается: ни кнопок, ни событий.</summary>
        Off,
        /// <summary>Сборка модуля не загружается совсем — для модуля, который падает уже при загрузке.</summary>
        NoLoad
    }

    /// <summary>
    /// Режимы модулей у всех (src\Core\DECISIONS.md, «Модули и кнопки — одним файлом»): блок {Модули} файла ribbon.txt в
    /// сборке (правит и выпускает только разработчик; раньше — Config\modules.txt на сервере, затем modules.txt в сборке),
    /// строка на модуль — «&lt;код модуля&gt; = работает | только тест | выключен | не загружать». «Без кнопок» больше нет:
    /// кнопки видны всегда, кому недоступны — серые (ButtonAccess). Код модуля — IModule.Id,
    /// он же окончание имени сборки (RASv2.&lt;код&gt;.dll): по нему «не загружать» работает до загрузки сборки.
    /// Действует после перезапуска Revit. Без Revit API: проверяется тестами.
    /// </summary>
    internal static class ModuleSwitches
    {
        private static readonly Dictionary<string, ModuleMode> Words = new Dictionary<string, ModuleMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["работает"] = ModuleMode.Works,
            ["только тест"] = ModuleMode.TestOnly,
            ["выключен"] = ModuleMode.Off,
            ["не загружать"] = ModuleMode.NoLoad
        };

        /// <summary>Разобрать файл. Ошибки не мешают: строка пропускается (модуль работает как обычно), причина — в warn.</summary>
        public static Dictionary<string, ModuleMode> Parse(IEnumerable<string> lines, Action<string> warn)
        {
            var result = new Dictionary<string, ModuleMode>(StringComparer.OrdinalIgnoreCase);
            var number = 0;
            foreach (var raw in lines)
            {
                number++;
                var line = raw;
                var hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;
                var eq = line.IndexOf('=');
                var code = eq > 0 ? line.Substring(0, eq).Trim() : "";
                var word = eq > 0 ? Normalize(line.Substring(eq + 1)) : "";
                if (code.Length == 0 || !Words.TryGetValue(word, out var mode))
                {
                    warn("ribbon.txt, {Модули}, строка " + number + ": «" + raw.Trim() + "» не понята — пропущена" +
                         (word == "без кнопок" ? " (режима «без кнопок» больше нет: кнопка видна всегда, недоступная — серая, «доступна=» в раскладке)" : ""));
                    continue;
                }
                result[code] = mode; // модуль указан дважды — действует последняя строка
            }
            return result;
        }

        /// <summary>Режим модуля; нет в файле — работает как обычно.</summary>
        public static ModuleMode ModeOf(IReadOnlyDictionary<string, ModuleMode> switches, string code) =>
            switches != null && code != null && switches.TryGetValue(code, out var mode) ? mode : ModuleMode.Works;

        /// <summary>Канал тестовой группы — «тест»; у разработчика (сборки из out\Releases) — «разработка».</summary>
        public static bool IsTestChannel(string channel) =>
            string.Equals(channel, "test", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(channel, "dev", StringComparison.OrdinalIgnoreCase);

        /// <summary>«Только   тест» → «только тест»: лишние пробелы между словами не ошибка.</summary>
        private static string Normalize(string s) => string.Join(" ", s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }
}
