using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RASv2.ClashAnalysis.Bridge;
using RASv2.ClashAnalysis.Logic;
using RASv2.ClashAnalysis.Reports;
using RASv2.ClashAnalysis.Site.Data;
using RASv2.ClashAnalysis.Site.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Clash Analysis, мост в Revit: какая открытая модель или связь — модель элемента коллизии (ModelNames).</summary>
    internal static class ClashBridgeTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static readonly string[] Open = { "3479_01_07_S01_AR01_01_M3.rvt", "3479_01_07_S01_OV01_01_M3", "3479_01_07_S01_KJ01_01_M3.rvt" };

        public static IEnumerable<KeyValuePair<string, Action>> All()
        {
            yield return Case("имя без папки и расширения", () =>
            {
                Check(ModelNames.Bare(@"D:\Модели\Дом_AR01.nwc") == "Дом_AR01", "папка и .nwc");
                Check(ModelNames.Bare("Дом_AR01.rvt") == "Дом_AR01", ".rvt");
                Check(ModelNames.Bare("Дом_AR01.v2") == "Дом_AR01.v2", "чужое расширение — часть имени");
            });
            yield return Case("точное совпадение — без расширения и регистра", () =>
            {
                Check(ModelNames.Match("3479_01_07_S01_AR01_01_M3.nwc", Open) == 0, "nwc ↔ rvt");
                Check(ModelNames.Match("3479_01_07_s01_ov01_01_m3.NWC", Open) == 1, "регистр, связь без расширения");
                Check(ModelNames.Match("3479_01_07_S01_VK01_01_M3.nwc", Open) == -1, "такой модели нет");
                Check(ModelNames.Match("", Open) == -1, "пусто");
            });
            yield return Case("русские буквы, похожие на латинские, — одно и то же", () =>
            {
                Check(ModelNames.Match("3479_01_07_S01_KJ01_01_М3.nwc", Open) == 2, "«М3» кириллицей");
                Check(ModelNames.Key("Ёлка") == ModelNames.Key("елка"), "ё = е");
            });
            yield return Case("приписка к NWC после разделителя — та же модель", () =>
            {
                Check(ModelNames.Match("3479_01_07_S01_KJ01_01_M3_с лестницей_BIM_Alma.nwc", Open) == 2, "«_с лестницей_BIM_Alma»");
                Check(ModelNames.Match("3479_01_07_S01_AR01_01_M3_отсоединено.nwc", Open) == 0, "«_отсоединено»");
                Check(ModelNames.Match("3479_01_07_S01_AR01_01_M35.nwc", Open) == -1, "«M35» — не «M3»: после «M3» нет разделителя");
            });
            yield return Case("из нескольких подходящих — самая длинная", () =>
            {
                var open = new[] { "Дом_AR01", "Дом_AR01_01" };
                Check(ModelNames.Match("Дом_AR01_01_отсоединено.nwc", open) == 1, "«Дом_AR01_01», а не «Дом_AR01»");
            });
            yield return Case("просьба сайта (JSON браузера) читается, ответ — по договорённости", () =>
            {
                var json = "{\"version\":1,\"clash\":\"Конфликт14\",\"elements\":[{\"model\":\"Дом_AR01.nwc\",\"id\":11634563,\"category\":\"Стены\"}," +
                           "{\"model\":\"Дом_OV01.nwc\",\"id\":10184727,\"extra\":\"незнакомое поле\"}]}";
                var r = BridgeProtocol.Read<ShowRequest>(Encoding.UTF8.GetBytes(json));
                Check(r.Version == 1 && r.Clash == "Конфликт14" && r.Elements.Count == 2, "просьба целиком");
                Check(r.Elements[0].Model == "Дом_AR01.nwc" && r.Elements[0].Id == 11634563 && r.Elements[0].Category == "Стены", "первый элемент");
                Check(r.Elements[1].Id == 10184727 && r.Elements[1].Category == null, "второй: незнакомое поле не мешает, нет категории — пусто");
                var reply = BridgeProtocol.Text(BridgeProtocol.Write(new ShowReply { Ok = true, Warnings = { "Связь «Дом_OV01» выгружена" } }));
                Check(reply.Contains("\"ok\":true") && reply.Contains("Дом_OV01") && reply.Contains("\"version\":1"), "ответ: " + reply);
            });
            yield return Case("настройки вида: по умолчанию, запись и чтение, испорченный файл", () =>
            {
                var d = new ViewSettings();
                Check(d.Kind == ViewSettings.Kind3D && !d.Isolate && d.MarginBySize && !d.CropToView && d.Style == ViewSettings.StyleShaded && d.Paint
                      && d.ClashTransparency == 0 && d.OtherTransparency == 0 && d.WarnUnloaded && d.WarnMissingLink,
                      "по умолчанию: 3D, по размеру, «Тонированный», красить, без прозрачности, предупреждения");
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rasv2-tests-" + Guid.NewGuid().ToString("N"));
                var path = System.IO.Path.Combine(dir, "view.json");
                try
                {
                    Check(ViewSettings.Load(path).Kind == ViewSettings.Kind3D, "файла нет — по умолчанию");
                    var s = new ViewSettings { Kind = ViewSettings.KindAuto, Isolate = true, MarginBySize = false, MarginMeters = 2.5, CropToView = true,
                        Style = ViewSettings.StyleHidden, Paint = false, ClashTransparency = 20, OtherTransparency = 70, WarnUnloaded = false };
                    s.Save(path);
                    s.Save(path); // повторная запись — заменой
                    var r = ViewSettings.Load(path);
                    Check(r.Kind == ViewSettings.KindAuto && r.Isolate && !r.MarginBySize && r.MarginMeters == 2.5 && r.CropToView && r.Style == ViewSettings.StyleHidden
                          && !r.Paint && r.ClashTransparency == 20 && r.OtherTransparency == 70 && !r.WarnUnloaded && r.WarnMissingLink, "записано — прочитано так же");
                    System.IO.File.WriteAllText(path, "{\"kind\":\"plan\"}");
                    r = ViewSettings.Load(path);
                    Check(r.Kind == ViewSettings.KindPlan && r.Paint && r.MarginBySize && !r.CropToView && r.OtherTransparency == 0 && r.WarnUnloaded,
                        "старый файл без новых полей — новые по умолчанию");
                    System.IO.File.WriteAllText(path, "{\"kind\":\"cube\",\"style\":\"neon\",\"marginMeters\":500,\"clashTransparency\":-5,\"otherTransparency\":300}");
                    r = ViewSettings.Load(path);
                    Check(r.Kind == ViewSettings.Kind3D && r.Style == ViewSettings.StyleShaded && r.MarginMeters == ViewSettings.MaxMargin
                          && r.ClashTransparency == 0 && r.OtherTransparency == ViewSettings.MaxTransparency, "незнакомое — по умолчанию, запас и прозрачность — в пределах");
                    System.IO.File.WriteAllText(path, "не JSON");
                    Check(ViewSettings.Load(path).Kind == ViewSettings.Kind3D, "испорченный файл — по умолчанию");
                }
                finally
                {
                    if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
                }
            });
            yield return Case("«Авто»: плита — план, стена — разрез, остальное — 3D", () =>
            {
                Check(ViewRules.KindFor(ViewSettings.KindAuto, new[] { "OST_Floors", "OST_DuctCurves" }) == ViewSettings.KindPlan, "перекрытие × воздуховод — план");
                Check(ViewRules.KindFor(ViewSettings.KindAuto, new[] { "OST_StructuralFoundation", "OST_PipeCurves" }) == ViewSettings.KindPlan, "фундаментная плита — план");
                Check(ViewRules.KindFor(ViewSettings.KindAuto, new[] { "OST_Walls", "OST_DuctCurves" }) == ViewSettings.KindSection, "стена — разрез");
                Check(ViewRules.KindFor(ViewSettings.KindAuto, new[] { "OST_Walls", "OST_Floors" }) == ViewSettings.KindPlan, "стена × плита — план (плита первой)");
                Check(ViewRules.KindFor(ViewSettings.KindAuto, new[] { "OST_Ceilings", "OST_Roofs" }) == ViewSettings.Kind3D, "потолки и крыши — 3D");
                Check(ViewRules.KindFor(ViewSettings.KindSection, new[] { "OST_Floors" }) == ViewSettings.KindSection, "не «Авто» — как выбрано");
            });
            yield return Case("запас обрезки: по размеру — половина пары, не меньше 0,5 м; иначе — заданный", () =>
            {
                var s = new ViewSettings();
                Check(ViewRules.MarginMeters(s, 0.4) == 0.5 && ViewRules.MarginMeters(s, 6) == 3, "по размеру");
                s.MarginBySize = false;
                s.MarginMeters = 1.5;
                Check(ViewRules.MarginMeters(s, 6) == 1.5, "заданный");
                s.CropToView = true;
                Check(ViewRules.CropToView(s, ViewSettings.Kind3D) && !ViewRules.CropToView(s, ViewSettings.KindPlan) && !ViewRules.CropToView(s, ViewSettings.KindSection),
                    "«Границы вида» — только у 3D-вида");
            });
            yield return Case("категории элементов: хранятся скрытые, подкатегории — везде, копия не делит списки", () =>
            {
                var s = new ViewSettings();
                Check(s.HiddenHost.Count == 0 && s.HiddenLink.Count == 0 && CategoryChoice.Summary(s) == "Все категории видны", "по умолчанию видно всё");
                var sub = CategoryChoice.SubKey("OST_Walls", "Отделка");
                Check(sub == "OST_Walls|Отделка" && CategoryChoice.IsSub(sub) && !CategoryChoice.IsSub("OST_Walls"), "ключ подкатегории");
                s.HiddenLink = new List<string> { "OST_GenericModel", " ", "OST_GenericModel", sub };
                s.HiddenHost = new List<string> { "OST_Walls" };
                s.Normalize();
                Check(s.HiddenLink.Count == 2 && CategoryChoice.SubHidden(s, sub) && !CategoryChoice.SubHidden(s, "OST_Walls|Несущий слой"),
                    "пустые и повторы — прочь; подкатегория скрыта хоть в одном — скрыта");
                Check(CategoryChoice.Summary(s) == "Скрыто: в исходном файле — 1, в связанном — 2", "подпись у кнопки: " + CategoryChoice.Summary(s));
                var copy = s.Copy();
                copy.HiddenHost.Add("OST_Floors");
                Check(s.HiddenHost.Count == 1, "копия — свои списки (окно «Отмена» не меняет настройки)");
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rasv2-tests-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(dir);
                try
                {
                    var path = System.IO.Path.Combine(dir, "view.json");
                    s.Save(path);
                    var r = ViewSettings.Load(path);
                    Check(r.HiddenHost.SequenceEqual(s.HiddenHost) && r.HiddenLink.SequenceEqual(s.HiddenLink), "настройки — записаны и прочитаны");
                    var file = System.IO.Path.Combine(dir, "Категории вида.json");
                    CategoryFile.Save(file, new[] { "OST_Walls" }, new[] { "OST_GenericModel", "OST_GenericModel" });
                    var f = CategoryFile.Load(file);
                    Check(f.HiddenHost.SequenceEqual(new[] { "OST_Walls" }) && f.HiddenLink.SequenceEqual(new[] { "OST_GenericModel" }), "экспорт — импорт");
                    Check(System.IO.File.ReadAllText(file).Contains(CategoryFile.Mark), "в файле — пометка, что это за файл");
                    var thrown = false;
                    try { CategoryFile.Load(path); } catch (System.IO.InvalidDataException) { thrown = true; }
                    Check(thrown, "чужой файл (настройки вида) — не импортируется");
                    System.IO.File.WriteAllText(file, "не JSON");
                    thrown = false;
                    try { CategoryFile.Load(file); } catch (System.IO.InvalidDataException) { thrown = true; }
                    Check(thrown, "испорченный файл — понятная ошибка");
                }
                finally
                {
                    System.IO.Directory.Delete(dir, true);
                }
            });
            yield return Case("прозрачность: и при «Изолировать»; остальные прозрачны — у элементов коллизии не меньше 1 %", () =>
            {
                var s = new ViewSettings();
                Check(ViewRules.Transparency(s) == (0, 0), "по умолчанию — нет");
                s.ClashTransparency = 30;
                Check(ViewRules.Transparency(s) == (30, 0), "только элементы коллизии");
                s.ClashTransparency = 0;
                s.OtherTransparency = 60;
                Check(ViewRules.Transparency(s) == (1, 60), "остальные — элементы коллизии 1 %, а не «не задано»");
                s.ClashTransparency = 25;
                Check(ViewRules.Transparency(s) == (25, 60), "обе заданы — как заданы");
                s.Isolate = true;
                Check(ViewRules.Transparency(s) == (25, 60), "«Изолировать» — так же");
            });
            yield return Case("место коллизии по габаритам: общая часть, промежуток, вложенный", () =>
            {
                // труба 2 м поперёк перекрытия 40 м: по оси перекрытия — только ширина трубы
                Check(ClashZone.Axis(0, 40, 19.9, 20.1) == (19.9, 20.1), "общая часть — ширина трубы, а не всё перекрытие");
                Check(ClashZone.Axis(0, 1, 1.05, 3) == (1, 1.05), "не касаются (ближе допуска) — промежуток между ними");
                Check(ClashZone.Axis(5, 6, 0, 10) == (5, 6), "один внутри другого — меньший");
                Check(ClashZone.Axis(0, 1, 1, 2) == (1, 1), "касаются — точка");
            });
            yield return Case("«Коллизии элемента»: точно номер и модель, а не часть номера", () =>
            {
                ClashRow Row(long id, string modelA, long idA, string modelB, long idB) => new ClashRow
                {
                    Id = id,
                    A = new ElementView { ModelFile = modelA, ElementId = idA, Family = "Базовая стена", Type = "Бетон_200" },
                    B = new ElementView { ModelFile = modelB, ElementId = idB, Category = "Воздуховоды" },
                };
                var rows = new[]
                {
                    Row(1, "Дом_AR01_отсоединено.nwc", 100, "Дом_OV01.nwc", 7),
                    Row(2, "Дом_AR01.nwc", 100, "Дом_VK01.nwc", 8),
                    Row(3, "Дом_KJ01.nwc", 100, "Дом_OV01.nwc", 9),       // тот же номер — другая модель
                    Row(4, "Дом_AR01.nwc", 1000, "Дом_OV01.nwc", 10),     // номер содержит «100» — не он
                };
                var keys = ClashFacets.ElementKeys(rows, new[] { ("Дом_AR01.rvt", 100L) });
                var sel = new Dictionary<string, HashSet<string>> { [ClashFacets.Element] = keys };
                var found = rows.Where(r => ClashFacets.Matches(r, sel)).Select(r => r.Id).ToList();
                Check(string.Join(",", found) == "1,2", "коллизии 1 и 2 (в т. ч. отсоединённая копия), не 3 и 4: " + string.Join(",", found));
                Check(ClashFacets.ElementTitle(rows[0].A) == "Базовая стена Бетон_200 (ID 100)", "метка элемента");
                Check(ClashFacets.ElementKeys(rows, new[] { ("Дом_AR01.rvt", 555L) }).Count == 0, "нет такого — пусто");
            });
            yield return Case("мост: просьбы — только со своего сайта (имя, полное имя в домене, этот компьютер)", () =>
            {
                const string site = "http://WIN-QJI8IV2IK11:5080";
                Check(SiteOrigins.Allowed("http://win-qji8iv2ik11:5080", site), "имя компьютера (браузер пишет строчными)");
                Check(SiteOrigins.Allowed("http://win-qji8iv2ik11.ras.kz:5080", site), "полное имя в домене");
                Check(SiteOrigins.Allowed("http://localhost:5080", site) && SiteOrigins.Allowed("http://127.0.0.1:5080", site), "разработчик у себя");
                Check(!SiteOrigins.Allowed("http://evil.example:5080", site) && !SiteOrigins.Allowed("http://win-qji8iv2ik11:5081", site)
                      && !SiteOrigins.Allowed("https://win-qji8iv2ik11:5080", site) && !SiteOrigins.Allowed("http://win-qji8iv2ik11x:5080", site)
                      && !SiteOrigins.Allowed(null, site) && !SiteOrigins.Allowed("null", site), "чужие — нет");
            });
            yield return Case("открыта отсоединённая копия — тоже находится", () =>
            {
                var open = new[] { "Дом_AR01_отсоединено" };
                Check(ModelNames.Match("Дом_AR01.nwc", open) == 0, "в отчёте без приписки");
            });
        }
    }
}
