using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RASv2.ClashAnalysis.Reports;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis: чтение отчётов Navisworks (ClashReportReader) и правила над ними (ClashRules).</summary>
    internal static class ClashReportTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static readonly DateTime FileTime = new DateTime(2026, 9, 15, 11, 57, 0);

        private static ClashReport Parse(string xml, List<string> warnings = null) =>
            ClashReportReader.Parse(new StringReader(xml), @"C:\Отчёты\S08\КЖ_МЕР.xml", FileTime, w => warnings?.Add(w));

        /// <summary>Образец Navisworks Manage 2023 (S08, 15.09.2026), сокращён до одной коллизии.</summary>
        private const string Sample = @"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<exchange xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" units=""m"" filename="""" filepath="""">
  <batchtest name=""Report"" internal_name=""Report"" units=""m"">
    <clashtests>
      <clashtest name=""КЖ Вертикальные конструкции/МЕР Воздуховоды"" test_type=""hard"" status=""ok"" tolerance=""0.050"" merge_composites=""0"">
        <linkage mode=""none""/>
        <left><clashselection selfintersect=""0"" primtypes=""1""><locator>lcop_selection_set_tree/КЖ Вертикальные конструкции</locator></clashselection></left>
        <right><clashselection selfintersect=""0"" primtypes=""1""><locator>lcop_selection_set_tree/MEP Воздуховоды</locator></clashselection></right>
        <rules/>
        <summary total=""2"" new=""2"" active=""0"" reviewed=""0"" approved=""0"" resolved=""0"">
          <testtype>По пересечению</testtype>
          <teststatus>ОК</teststatus>
        </summary>
        <clashresults>
          <clashresult name=""Конфликт1"" guid=""DF548635-21ba-4ac1-82f8-40b109a8a204"" href=""КЖ Вертикальные конструкции_МЕР Воздуховоды_files\cd040001.jpg"" status=""new"">
            <resultstatus>Создать</resultstatus>
            <clashpoint><pos3f x=""11.196"" y=""-63.464"" z=""698.850""/></clashpoint>
            <clashobjects>
              <clashobject>
                <pathlink>
                  <node>Файл</node><node>Файл</node>
                  <node>0000_01_01_S08_KJ01_01_M3_с лестницей_BIM_Alma.nwc</node>
                  <node>-01 Подвал</node><node>Стены</node><node>Базовая стена</node>
                  <node>(стена)железобетон_t=200 w8 f150</node><node>Базовая стена</node>
                  <node>BI_бетон_железобетон_C20/25(W8 F150)</node>
                </pathlink>
                <smarttags>
                  <smarttag><name>Элемент Имя</name><value>BI_бетон_железобетон_C20/25(W8 F150)</value></smarttag>
                  <smarttag><name>Объект Id</name><value>2598747</value></smarttag>
                </smarttags>
              </clashobject>
              <clashobject>
                <pathlink>
                  <node>Файл</node><node>Файл</node>
                  <node>3457_01_01_S8_OV01_01_М3.nwc</node>
                  <node>-01 Подвал</node><node>Воздуховоды</node><node>Воздуховод прямоугольного сечения</node>
                  <node>(воздуховод)без_огнезащиты_врезки_ГОСТ 14918-80</node><node>Воздуховод прямоугольного сечения</node>
                  <node>Твердое тело</node>
                </pathlink>
                <smarttags>
                  <smarttag><name>Элемент Имя</name><value>Воздуховод прямоугольного сечения</value></smarttag>
                  <smarttag><name>Объект Id</name><value>9955339</value></smarttag>
                </smarttags>
              </clashobject>
            </clashobjects>
          </clashresult>
        </clashresults>
      </clashtest>
    </clashtests>
    <selectionsets/>
  </batchtest>
</exchange>";

        /// <summary>Чего нет в образцах: группа, дата обнаружения, расстояние, оси, английский Navisworks, коллизия без guid.</summary>
        private const string Extended = @"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<exchange units=""mm"">
  <batchtest name=""Report"">
    <clashtests>
      <clashtest name=""АР Стены/МЕР Трубы"" test_type=""clearance"" tolerance=""50"">
        <clashresults>
          <clashgroup name=""Стояк в осях Г-4"" guid=""aaaa"">
            <clashresults>
              <clashresult name=""Конфликт7"" guid=""11111111-2222-3333-4444-555555555555"" status=""approved"" distance=""-0.068"">
                <description>Трубы через стену</description>
                <gridlocation>Г-4 : 02 Этаж</gridlocation>
                <createddate><date year=""2026"" month=""10"" day=""1"" hour=""9"" minute=""5"" second=""0""/></createddate>
                <clashobjects>
                  <clashobject>
                    <pathlink><node>Файл</node><node>Файл</node><node>AR.rvt</node><node>2 Этаж</node><node>Стены</node></pathlink>
                    <smarttags><smarttag><name>Element ID</name><value>101</value></smarttag><smarttag><name>Item Name</name><value>Кирпич</value></smarttag></smarttags>
                  </clashobject>
                  <clashobject>
                    <pathlink><node>Файл</node><node>Файл</node><node>VK.nwc</node><node>02 Этаж</node><node>Трубы</node></pathlink>
                    <smarttags><smarttag><name>Element ID</name><value>202</value></smarttag></smarttags>
                  </clashobject>
                </clashobjects>
              </clashresult>
            </clashresults>
          </clashgroup>
          <clashresult name=""Конфликт8"" status=""resolved"">
            <createddate><date year=""2026"" month=""10"" day=""3"" hour=""14"" minute=""0"" second=""0""/></createddate>
            <clashobjects>
              <clashobject>
                <pathlink><node>Файл</node><node>Файл</node><node>VK.nwc</node><node>02 Этаж</node><node>Трубы</node></pathlink>
                <smarttags><smarttag><name>Element ID</name><value>202</value></smarttag></smarttags>
              </clashobject>
              <clashobject>
                <pathlink><node>Файл</node><node>Файл</node><node>AR.rvt</node><node>2 Этаж</node><node>Стены</node></pathlink>
                <smarttags><smarttag><name>Element ID</name><value>101</value></smarttag></smarttags>
              </clashobject>
            </clashobjects>
          </clashresult>
          <clashresult name=""Пустая"" guid=""bbbb"" status=""new""/>
        </clashresults>
      </clashtest>
    </clashtests>
  </batchtest>
</exchange>";

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("образец Navisworks 2023: проверка, наборы, итог, коллизия, точка", () =>
            {
                var r = Parse(Sample);
                Check(r.Units == "m" && r.Tests.Count == 1, "единицы / проверки");
                var t = r.Tests[0];
                Check(t.Name == "КЖ Вертикальные конструкции/МЕР Воздуховоды", "имя проверки: " + t.Name);
                Check(t.TestType == "hard" && Math.Abs(t.Tolerance - 0.05) < 1e-9, "тип / допуск");
                Check(t.LeftSet == "КЖ Вертикальные конструкции" && t.RightSet == "MEP Воздуховоды", "наборы: " + t.LeftSet + " / " + t.RightSet);
                Check(t.Summary.Total == 2 && t.Summary.New == 2, "итог");
                var c = t.Results.Single();
                Check(c.Name == "Конфликт1" && c.Guid == "df548635-21ba-4ac1-82f8-40b109a8a204", "имя / guid (нижний регистр)");
                Check(c.Status == NavisworksStatus.New, "статус");
                Check(c.Point != null && Math.Abs(c.Point.Y + 63.464) < 1e-9 && Math.Abs(c.Point.Z - 698.85) < 1e-9, "точка");
                Check(c.Distance == null && c.Created == null && c.GridLocation == null && c.GroupName == null, "необязательных полей нет");
            });
            yield return Case("элемент: файл модели, уровень, категория, семейство, тип, имя, номер", () =>
            {
                var c = Parse(Sample).Tests[0].Results[0];
                Check(c.Elements.Count == 2, "элементов: " + c.Elements.Count);
                var kj = c.Elements[0];
                Check(kj.ModelFile == "0000_01_01_S08_KJ01_01_M3_с лестницей_BIM_Alma.nwc", "файл: " + kj.ModelFile);
                Check(kj.Level == "-01 Подвал" && kj.Category == "Стены", "уровень / категория");
                Check(kj.Family == "Базовая стена" && kj.Type == "(стена)железобетон_t=200 w8 f150", "семейство / тип");
                Check(kj.Name == "BI_бетон_железобетон_C20/25(W8 F150)" && kj.ElementId == 2598747, "имя / номер");
                Check(kj.Path.Count == 9, "путь целиком");
                Check(c.Elements[1].ElementId == 9955339 && c.Elements[1].Category == "Воздуховоды", "второй элемент");
            });
            yield return Case("дата отчёта: без дат обнаружения — время файла", () =>
            {
                var r = Parse(Sample);
                Check(r.Date == FileTime && !r.DateFromReport, "дата: " + r.Date);
            });
            yield return Case("группы, дата обнаружения, расстояние, оси, английский Navisworks", () =>
            {
                var warnings = new List<string>();
                var r = Parse(Extended, warnings);
                var t = r.Tests[0];
                Check(r.Units == "mm" && t.TestType == "clearance" && t.Tolerance == 50, "единицы / тип / допуск");
                Check(t.Results.Count == 2, "коллизий: " + t.Results.Count);
                var g = t.Results[0];
                Check(g.GroupName == "Стояк в осях Г-4", "группа: " + g.GroupName);
                Check(g.Status == NavisworksStatus.Approved && g.Distance == -0.068, "статус / расстояние");
                Check(g.GridLocation == "Г-4 : 02 Этаж" && g.Description == "Трубы через стену", "оси / описание");
                Check(g.Created == new DateTime(2026, 10, 1, 9, 5, 0), "дата обнаружения");
                Check(g.Elements[0].ElementId == 101 && g.Elements[0].Name == "Кирпич" && g.Elements[0].ModelFile == "AR.rvt", "Element ID / Item Name / .rvt");
                Check(t.Results[1].GroupName == null && t.Results[1].Guid == null, "коллизия вне группы, без guid");
                Check(warnings.Count == 1 && warnings[0].Contains("«Пустая»"), "коллизия без элементов пропущена с предупреждением");
            });
            yield return Case("дата отчёта — самая поздняя дата обнаружения", () =>
            {
                var r = Parse(Extended);
                Check(r.DateFromReport && r.Date == new DateTime(2026, 10, 3, 14, 0, 0), "дата: " + r.Date);
            });
            yield return Case("не отчёт или не XML — понятная причина", () =>
            {
                string Error(string xml)
                {
                    try { Parse(xml); }
                    catch (InvalidDataException ex) { return ex.Message; }
                    return null;
                }
                Check(Error("<html><body>отчёт</body></html>")?.Contains("не отчёт Navisworks") == true, "HTML");
                Check(Error("<exchange units=\"m\"></exchange>")?.Contains("не отчёт Navisworks") == true, "без проверок");
                Check(Error("<exchange><clashtest")?.Contains("не читается как XML") == true, "обрыв файла");
            });
            yield return Case("«личность»: проверка + номера двух элементов, порядок не важен; номер Navisworks — только без пары", () =>
            {
                var t = Parse(Extended).Tests[0];
                var withGuid = ClashRules.IdentityKey(t.Name, t.Results[0]);
                Check(withGuid.StartsWith("ids:", StringComparison.Ordinal), "с guid и полной парой — по номерам: " + withGuid);
                var partial = new ClashResult { Guid = "g" };
                partial.Elements.Add(new ClashElement { ModelFile = "AR.nwc" });
                Check(ClashRules.IdentityKey(t.Name, partial) == "guid:g", "пара неполная — по guid");
                var noGuid = ClashRules.IdentityKey(t.Name, t.Results[1]);
                Check(noGuid == "ids:ар стены/мер трубы|101|202", "без guid: " + noGuid);
                var swapped = new ClashResult();
                swapped.Elements.Add(t.Results[1].Elements[1]);
                swapped.Elements.Add(t.Results[1].Elements[0]);
                Check(ClashRules.IdentityKey("АР  Стены/МЕР Трубы ", swapped) == noGuid, "другой порядок элементов и пробелы в имени");
            });
            yield return Case("уровень: ведущие нули, регистр и пробелы не различаются", () =>
            {
                Check(ClashRules.LevelKey("05 Этаж") == ClashRules.LevelKey("5 Этаж"), "05 = 5");
                Check(ClashRules.LevelKey("-01 Подвал") == ClashRules.LevelKey("-1  подвал"), "-01 = -1");
                Check(ClashRules.LevelKey("100 Кровля") != ClashRules.LevelKey("10 Кровля"), "100 ≠ 10");
                Check(ClashRules.LevelKey("00 Этаж") == ClashRules.LevelKey("0 Этаж"), "ноль");
                Check(ClashRules.LevelKey("Кровля") == "кровля" && ClashRules.LevelKey(null) == "", "без номера / пусто");
            });
            yield return Case("статусы Navisworks → наши", () =>
            {
                Check(ClashRules.StateOf(NavisworksStatus.New) == ClashState.Active, "новая");
                Check(ClashRules.StateOf(NavisworksStatus.Reviewed) == ClashState.Active, "проверенная");
                Check(ClashRules.StateOf(NavisworksStatus.Approved) == ClashState.Active, "подтверждённая — активная (статусы ставит только сайт)");
                Check(ClashRules.StateOf(NavisworksStatus.Resolved) == ClashState.Fixed, "исправленная");
                Check(ClashRules.Title(ClashState.Accepted) == "Допущена", "название");
            });
            yield return Case("снимок — рядом с отчётом; модель — имя без расширения", () =>
            {
                var r = Parse(Sample);
                var image = ClashRules.ImageFile(r, r.Tests[0].Results[0]);
                Check(image == @"C:\Отчёты\S08\КЖ Вертикальные конструкции_МЕР Воздуховоды_files\cd040001.jpg", "снимок: " + image);
                Check(ClashRules.ModelName("3457_01_01_S8_AR01_01_M3.nwc") == "3457_01_01_S8_AR01_01_M3", "без .nwc");
                Check(ClashRules.ModelName("AR.RVT") == "AR" && ClashRules.ModelName("Модель.v2") == "Модель.v2", "только расширения моделей");
                Check(Math.Abs(ClashRules.MetersPerUnit("mm") - 0.001) < 1e-12 && ClashRules.MetersPerUnit("m") == 1, "единицы");
            });
        }
    }
}
