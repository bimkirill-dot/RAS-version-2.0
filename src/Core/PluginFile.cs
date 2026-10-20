using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.Core
{
    /// <summary>
    /// Файл ribbon.txt в сборке — всё, что разработчик задаёт для всех (src\Core\DECISIONS.md, «Модули и кнопки — одним
    /// файлом»): блок {Модули} — какие модули работают (ModuleSwitches), блок {Люди} — группы людей для кнопок (ButtonAccess),
    /// остальное — раскладка ленты (RibbonLayout). Блок длится до следующего «{…}» или до первой панели / отдела «[…]».
    /// Строки чужих частей заменяются пустыми — номера строк в предупреждениях остаются настоящими. Без Revit API.
    /// </summary>
    internal sealed class PluginFile
    {
        public const string ModulesBlock = "Модули";
        public const string PeopleBlock = "Люди";

        public string[] Modules { get; private set; }
        public string[] People { get; private set; }
        public string[] Layout { get; private set; }

        public static PluginFile Split(IList<string> lines, Action<string> warn)
        {
            var file = new PluginFile
            {
                Modules = new string[lines.Count],
                People = new string[lines.Count],
                Layout = new string[lines.Count],
            };
            for (var i = 0; i < lines.Count; i++) file.Modules[i] = file.People[i] = file.Layout[i] = "";
            string[] target = file.Layout;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = (lines[i] ?? "").Trim();
                if (line.StartsWith("{", StringComparison.Ordinal) && line.EndsWith("}", StringComparison.Ordinal))
                {
                    var name = line.Substring(1, line.Length - 2).Trim();
                    if (string.Equals(name, ModulesBlock, StringComparison.OrdinalIgnoreCase)) target = file.Modules;
                    else if (string.Equals(name, PeopleBlock, StringComparison.OrdinalIgnoreCase)) target = file.People;
                    else
                    {
                        warn(FileName + ", строка " + (i + 1) + ": непонятный блок «" + line + "» (есть {" + ModulesBlock + "} и {" + PeopleBlock + "}) — пропущен до следующего");
                        target = null;
                    }
                    continue;
                }
                // панель или отдел — дальше раскладка ленты
                if (line.StartsWith("[", StringComparison.Ordinal)) target = file.Layout;
                if (target != null) target[i] = lines[i] ?? "";
            }
            return file;
        }

        public const string FileName = RibbonLayout.FileName;
    }

    /// <summary>
    /// Кому кнопка доступна: «доступна="…"» в раскладке. Кнопка на ленте видна всегда; кому недоступна — серая (с пояснением
    /// в подсказке). Не указано или «всем» — всем; «никому» — никому; иначе — логины Windows и группы из блока {Люди}
    /// через запятую. Без Revit API — тесты.
    /// </summary>
    internal static class ButtonAccess
    {
        public const string Everyone = "всем";
        public const string Nobody = "никому";

        /// <summary>Группы из блока {Люди}: «руководители = Ivanov_A, Petrov_B». Ошибочная строка пропускается.</summary>
        public static Dictionary<string, List<string>> People(IEnumerable<string> lines, Action<string> warn)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var number = 0;
            foreach (var raw in lines)
            {
                number++;
                var line = raw ?? "";
                var hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;
                var eq = line.IndexOf('=');
                var name = eq > 0 ? line.Substring(0, eq).Trim() : "";
                var logins = eq > 0 ? Logins(line.Substring(eq + 1)) : new List<string>();
                if (name.Length == 0 || logins.Count == 0)
                {
                    warn(PluginFile.FileName + ", {" + PluginFile.PeopleBlock + "}, строка " + number + ": «" + raw.Trim() + "» не понята (нужно: группа = логин, логин) — пропущена");
                    continue;
                }
                if (string.Equals(name, Everyone, StringComparison.OrdinalIgnoreCase) || string.Equals(name, Nobody, StringComparison.OrdinalIgnoreCase))
                {
                    warn(PluginFile.FileName + ", {" + PluginFile.PeopleBlock + "}, строка " + number + ": «" + name + "» — служебное слово, группу так не назвать — пропущена");
                    continue;
                }
                groups[name] = logins; // группа указана дважды — действует последняя строка
            }
            return groups;
        }

        /// <summary>
        /// Доступна ли кнопка человеку user (логин Windows; домен «RAS\» не важен). Неизвестное слово — это логин: с ним кнопка
        /// доступна только человеку с таким логином (опечатку в группе видно в «О плагине»: кнопка серая у всех).
        /// </summary>
        public static bool Allowed(string access, string user, IReadOnlyDictionary<string, List<string>> people)
        {
            if (string.IsNullOrWhiteSpace(access)) return true;
            var me = Login(user);
            foreach (var token in Logins(access))
            {
                if (string.Equals(token, Everyone, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(token, Nobody, StringComparison.OrdinalIgnoreCase)) continue;
                if (people != null && people.TryGetValue(token, out var group))
                {
                    if (group.Any(l => string.Equals(l, me, StringComparison.OrdinalIgnoreCase))) return true;
                    continue;
                }
                if (string.Equals(token, me, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>«Ivanov_A, RAS\Petrov_B» → «Ivanov_A», «Petrov_B».</summary>
        private static List<string> Logins(string text) =>
            (text ?? "").Split(',').Select(Login).Where(s => s.Length > 0).ToList();

        private static string Login(string s)
        {
            s = (s ?? "").Trim();
            var slash = s.LastIndexOf('\\');
            return slash >= 0 ? s.Substring(slash + 1).Trim() : s;
        }
    }
}
