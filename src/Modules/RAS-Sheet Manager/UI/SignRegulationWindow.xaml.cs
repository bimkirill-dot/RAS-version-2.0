using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RASv2.SheetManager.Logic;

namespace RASv2.SheetManager.UI
{
    /// <summary>
    /// Регламент наименования файлов подписей — в плагине (если в настройках не указан свой документ). Слово, отделы и кто со
    /// знаком «!» — из настроек, так что регламент всегда совпадает с проверкой (SignFileName).
    /// </summary>
    public partial class SignRegulationWindow : Window
    {
        public SignRegulationWindow(SignSettings s)
        {
            InitializeComponent();
            RASv2.Core.WindowTheme.Apply(this);
            var word = s.FileWord;
            var leaders = SignFileName.List(s.Leaders);
            var departments = SignFileName.List(s.Departments);
            var dep = departments.FirstOrDefault() ?? "КЖ";
            var lead = leaders.FirstOrDefault() ?? "ГИП";
            Pattern.Text = "[Отдел]_" + word + "_Фамилия.dwg";
            Rules.Text =
                "• Части — через «_»: отдел, слово «" + word + "», фамилия; расширение — .dwg." + "\n" +
                "• Отдел — заглавными буквами" + (departments.Count > 0 ? ": " + string.Join(", ", departments) : " (КЖ, АР, ОВ, ВК, ЭЛ, ГП…)") + "." + "\n" +
                "• Главный специалист — «#» перед отделом." + "\n" +
                (leaders.Count > 0 ? "• " + string.Join(" и ", leaders) + " — «!» в начале, на месте отдела — " + string.Join(" или ", leaders) + "; у остальных «!» не ставится." + "\n" : "") +
                "• Фамилия — с заглавной буквы, как в штампе; инициал — через пробел; можно казахские буквы, дефис. Латиницу и кириллицу в одной фамилии не смешивать.";
            foreach (var example in new[]
            {
                dep + "_" + word + "_Иванов.dwg",
                "#" + dep + "_" + word + "_Петрова.dwg",
                "!" + lead + "_" + word + "_Әбілов.dwg",
                dep + "_" + word + "_Ким И.dwg",
            })
                Examples.Children.Add(new TextBlock { Text = example, Style = (Style)FindResource("Code") });
        }
    }
}
