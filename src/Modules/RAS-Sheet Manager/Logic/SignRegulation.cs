using System.Collections.Generic;
using System.Linq;

namespace RASv2.SheetManager.Logic
{
    /// <summary>
    /// Регламент наименования файлов подписей — текст в окне настроек плагина (разработчик, 27.10.2026: «регламент должен быть
    /// написан в самом плагине, в настройках»). Слово, отделы и кто со знаком «!» — из тех же настроек, что и проверка
    /// (SignFileName), так что текст и проверка не расходятся. Без Revit API — проверяется тестами.
    /// </summary>
    public static class SignRegulation
    {
        public static string Pattern(SignSettings s) => "[Отдел]_" + s.FileWord + "_Фамилия.dwg";

        public static List<string> Rules(SignSettings s)
        {
            var word = s.FileWord;
            var leaders = SignFileName.List(s.Leaders);
            var departments = SignFileName.List(s.Departments);
            var rules = new List<string>
            {
                "Части — через «_»: отдел, слово «" + word + "», фамилия; расширение — .dwg.",
                "Отдел — заглавными буквами" + (departments.Count > 0 ? ": " + string.Join(", ", departments) : " (КЖ, АР, ОВ, ВК, ЭЛ, ГП…)") + ".",
                "Главный специалист — «#» перед отделом.",
            };
            if (leaders.Count > 0)
                rules.Add(string.Join(" и ", leaders) + " — «!» в начале, на месте отдела — " + string.Join(" или ", leaders) + "; у остальных «!» не ставится.");
            rules.Add("Фамилия — с заглавной буквы, как в штампе; можно казахские буквы и дефис. Латиницу и кириллицу в одной фамилии не смешивать.");
            rules.Add("Инициал — только в двух случаях: у " + (leaders.Count > 0 ? string.Join(" и ", leaders) : "руководителей") +
                      " — в специальное поле таблицы выбора «" + s.KeyParam + "»; если специалистов с одинаковой фамилией двое — в имени файла, через пробел после фамилии, чтобы их не путать.");
            rules.Add("Подпись, названная не по регламенту, в семейство не добавляется: переименуйте файл и откройте папку в окне «Подпись» заново.");
            return rules;
        }

        public static List<string> Examples(SignSettings s)
        {
            var word = s.FileWord;
            var dep = SignFileName.List(s.Departments).FirstOrDefault() ?? "КЖ";
            var lead = SignFileName.List(s.Leaders).FirstOrDefault() ?? "ГИП";
            return new List<string>
            {
                dep + "_" + word + "_Иванов.dwg",
                "#" + dep + "_" + word + "_Петрова.dwg",
                "!" + lead + "_" + word + "_Әбілов.dwg",
                dep + "_" + word + "_Ким И.dwg  и  " + dep + "_" + word + "_Ким Н.dwg — однофамильцы",
            };
        }
    }
}
