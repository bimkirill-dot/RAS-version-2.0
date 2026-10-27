using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RASv2.SheetManager.Logic;

namespace RASv2.Tests
{
    /// <summary>RAS-Sheet Manager, «Подпись»: разбор DXF, очистка, имена, таблица выбора, план добавления.</summary>
    internal static class SheetSignTests
    {
        private static KeyValuePair<string, Action> Case(string name, Action body) => new KeyValuePair<string, Action>(name, body);

        private static void Check(bool condition, string what) => Program.Check(condition, what);

        private static bool Near(double a, double b, double eps = 1e-6) => Math.Abs(a - b) < eps;

        /// <summary>DXF из пар «код, значение».</summary>
        private static string Dxf(params object[] pairs)
        {
            var sb = new StringBuilder();
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                sb.Append(pairs[i]).Append("\n").Append(Convert.ToString(pairs[i + 1], System.Globalization.CultureInfo.InvariantCulture)).Append("\n");
            return sb.ToString();
        }

        private static string Entities(params object[] pairs) =>
            Dxf(new object[] { 0, "SECTION", 2, "ENTITIES" }.Concat(pairs).Concat(new object[] { 0, "ENDSEC", 0, "EOF" }).ToArray());

        public static IEnumerable<KeyValuePair<string, Action>> Geometry()
        {
            yield return Case("отрезок, лист (код 67) и невидимые (код 60) — не подпись", () =>
            {
                var doc = DxfDocument.Parse(Entities(
                    0, "LINE", 8, "0", 10, 0, 20, 0, 30, 0, 11, 10, 21, 0, 31, 0,
                    0, "LINE", 8, "0", 67, 1, 10, 0, 20, 0, 30, 0, 11, 99, 21, 99, 31, 0,
                    0, "LINE", 8, "0", 60, 1, 10, 0, 20, 0, 30, 0, 11, 50, 21, 50, 31, 0));
                var g = new SignGeometry(doc, 0.01);
                Check(doc.Model.Count == 2 && g.Curves.Count == 1 && Near(g.Curves[0].X[1], 10), "одна линия: " + g.Curves.Count);
            });
            yield return Case("полилиния с выпуклостью — полуокружность", () =>
            {
                // (0,0) → (2,0), выпуклость 1 — полукруг радиуса 1 против часовой: снизу, через (1,-1)
                var doc = DxfDocument.Parse(Entities(0, "LWPOLYLINE", 8, "0", 90, 2, 70, 0, 10, 0, 20, 0, 42, 1, 10, 2, 20, 0));
                var p = new SignGeometry(doc, 0.001).Curves[0];
                var ok = Enumerable.Range(0, p.Count).All(i => Near(Math.Sqrt((p.X[i] - 1) * (p.X[i] - 1) + p.Y[i] * p.Y[i]), 1, 1e-6));
                var minY = p.Y.Min();
                Check(ok && Near(minY, -1, 0.01) && p.Y.Max() < 1e-9 && Near(p.X.Last(), 2), "на окружности, снизу; точек " + p.Count);
            });
            yield return Case("блок: масштаб, поворот, зеркало (нормаль −Z), слой 0 — от вставки, скрытый слой", () =>
            {
                var text = Dxf(
                    0, "SECTION", 2, "TABLES", 0, "TABLE", 2, "LAYER",
                    0, "LAYER", 2, "Скрыт", 70, 0, 62, -7,
                    0, "ENDTAB", 0, "ENDSEC",
                    0, "SECTION", 2, "BLOCKS",
                    0, "BLOCK", 2, "Б", 10, 1, 20, 0, 30, 0,
                    0, "LINE", 8, "0", 10, 1, 20, 0, 30, 0, 11, 2, 21, 0, 31, 0,
                    0, "ENDBLK",
                    0, "ENDSEC",
                    0, "SECTION", 2, "ENTITIES",
                    // масштаб 2, поворот 90°: (1,0)-(2,0) блока → от базы (0,0)-(1,0) → ×2 → (0,0)-(0,2) → + (10,10)
                    0, "INSERT", 8, "0", 2, "Б", 10, 10, 20, 10, 30, 0, 41, 2, 42, 2, 50, 90,
                    // зеркало: нормаль (0,0,-1) — X вставки зеркален: (−5,0)+(0..1,0) → x = 5 .. 4
                    0, "INSERT", 8, "0", 2, "Б", 10, -5, 20, 0, 30, 0, 210, 0, 220, 0, 230, -1,
                    // на скрытом слое — вся вставка не берётся
                    0, "INSERT", 8, "Скрыт", 2, "Б", 10, 100, 20, 100, 30, 0,
                    0, "ENDSEC", 0, "EOF");
                var g = new SignGeometry(DxfDocument.Parse(text), 0.01);
                Check(g.Curves.Count == 2 && g.HiddenSkipped == 1, "линий " + g.Curves.Count + ", пропущено " + g.HiddenSkipped);
                var a = g.Curves[0];
                Check(Near(a.X[0], 10) && Near(a.Y[0], 10) && Near(a.X[1], 10, 1e-9) && Near(a.Y[1], 12), "поворот и масштаб: " + a.X[1] + "," + a.Y[1]);
                var b = g.Curves[1];
                Check(Near(b.X[0], 5) && Near(b.X[1], 4), "зеркало: " + b.X[0] + " → " + b.X[1]);
            });
            yield return Case("сплайн: прямая из 4 управляющих — точки на прямой, концы — в концах", () =>
            {
                var doc = DxfDocument.Parse(Entities(0, "SPLINE", 8, "0", 70, 8, 71, 3, 72, 8, 73, 4,
                    40, 0, 40, 0, 40, 0, 40, 0, 40, 1, 40, 1, 40, 1, 40, 1,
                    10, 0, 20, 0, 30, 0, 10, 1, 20, 1, 30, 0, 10, 2, 20, 2, 30, 0, 10, 3, 20, 3, 30, 0));
                var p = new SignGeometry(doc, 0.01).Curves[0];
                Check(Near(p.X[0], 0) && Near(p.X.Last(), 3) && Enumerable.Range(0, p.Count).All(i => Near(p.X[i], p.Y[i])), "точек " + p.Count);
            });
        }

        public static IEnumerable<KeyValuePair<string, Action>> Cleaning()
        {
            yield return Case("один размер: высота 7, ширина не больше 22; центр — в 0,0", () =>
            {
                // 100×10 единиц: высота 7 → ширина 70 > 22 → по ширине 22, высота 2,2
                var doc = DxfDocument.Parse(Entities(0, "LWPOLYLINE", 8, "0", 90, 3, 70, 0, 10, 0, 20, 0, 10, 100, 20, 0, 10, 100, 20, 10));
                var s = SignCleaner.Clean(doc, new CleanOptions(), out _);
                Check(Near(s.WidthMm, 22, 1e-9) && Near(s.HeightMm, 2.2, 1e-9), "размер " + s.WidthMm + "×" + s.HeightMm);
                var (minX, minY, maxX, maxY) = SignCleaner.Bounds(s.Lines);
                Check(Near(minX + maxX, 0, 1e-9) && Near(minY + maxY, 0, 1e-9), "центр");
                var tall = SignCleaner.Clean(DxfDocument.Parse(Entities(0, "LINE", 8, "0", 10, 0, 20, 0, 30, 0, 11, 1, 21, 2, 31, 0)), new CleanOptions(), out _);
                Check(Near(tall.HeightMm, 7, 1e-9) && Near(tall.WidthMm, 3.5, 1e-9), "по высоте: " + tall.WidthMm + "×" + tall.HeightMm);
                var keep = SignCleaner.Clean(doc, new CleanOptions { Mode = SizeModes.Keep }, out _);
                Check(Near(keep.WidthMm, 100, 1e-9), "как в DWG (мм)");
            });
            yield return Case("куски, стыкующиеся концами, — одна ломаная; лишние точки на прямой — убраны", () =>
            {
                var doc = DxfDocument.Parse(Entities(
                    0, "LINE", 8, "0", 10, 0, 20, 0, 30, 0, 11, 1, 21, 0, 31, 0,
                    0, "LINE", 8, "0", 10, 2, 20, 0, 30, 0, 11, 1, 21, 0, 31, 0,
                    0, "LINE", 8, "0", 10, 2, 20, 0, 30, 0, 11, 2, 21, 1, 31, 0));
                var s = SignCleaner.Clean(doc, new CleanOptions(), out _);
                Check(s.Lines.Count == 1 && s.Lines[0].Count == 3, "ломаных " + s.Lines.Count + ", точек " + s.Lines[0].Count);
            });
            yield return Case("рабочая папка AutoCAD — без пробелов и кириллицы (пробел в сценарии — Enter, AutoCAD ждал вечно)", () =>
            {
                Check(!SignFolder.ScriptSafe(@"C:\Users\u\AppData\Local\RAS_v_2.0\RAS-Sheet Manager\work") && !SignFolder.ScriptSafe(@"C:\Users\Иванов\Temp") && SignFolder.ScriptSafe(@"C:\Temp\RASv2_sign"), "проверка пути");
                var root = SignFolder.WorkRoot(@"C:\Users\u\AppData\Local\RAS_v_2.0\RAS-Sheet Manager\work");
                Check(SignFolder.ScriptSafe(root) && SignFolder.WorkRoot(@"C:\RAS\work") == @"C:\RAS\work", "запасная папка: " + root);
            });
            yield return Case("пустая подпись — причина; чистый DXF читается обратно", () =>
            {
                Check(SignCleaner.Clean(DxfDocument.Parse(Entities(0, "TEXT", 8, "0", 1, "Иванов")), new CleanOptions(), out var why) == null && why != null, "нет линий");
                var s = SignCleaner.Clean(DxfDocument.Parse(Entities(0, "CIRCLE", 8, "0", 10, 0, 20, 0, 30, 0, 40, 5)), new CleanOptions(), out _);
                var back = DxfDocument.Parse(DxfWriter.Write(s));
                var g = new SignGeometry(back, 0.01);
                Check(back.InsUnits == 4 && g.Curves.Count == 1 && g.Curves[0].Closed && g.Curves[0].Count == s.Lines[0].Count, "круг — замкнутая ломаная");
                Check(DxfWriter.Write(s).Contains("62\r\n5\r\n"), "слой 0 — синий (цвет 5)");
            });
        }

        public static IEnumerable<KeyValuePair<string, Action>> Names()
        {
            yield return Case("фамилия из имени файла", () =>
            {
                Check(SignNames.FromFile("#КЖ_Подпись_Иванов_clean.dwg", "Подпись", out var n1) == "Иванов" && n1 == null, "обычная");
                Check(SignNames.FromFile("ГИП Подпись Ахметов Б.dwg", "Подпись", out _) == "Ахметов Б", "пробелы");
                Check(SignNames.FromFile("КЖ_Подпись_Сидоровdwg.dwg", "Подпись", out var n2) == "Сидоров" && n2.Contains("dwg"), "лишнее dwg");
                Check(SignNames.FromFile("КЖ_Петров.dwg", "Подпись", out var n3) == "КЖ Петров" && n3 != null, "слова нет — всё имя и пометка");
                Check(SignNames.Problem("Әбілов") == null && SignNames.Problem("Иванов №2") == null && SignNames.Problem("Ива:нов") != null && SignNames.Problem("Ivan😀") != null, "допустимые знаки");
                Check(SignNames.ToRussian("Әбілқайыр") == "Абилкайыр", "казахские → русские");
            });
            yield return Case("формула и таблица выбора", () =>
            {
                var f = SignFormula.Parse("size_lookup(\"RAS_Таблица выбора фамилий\", \"Да/Нет\", 1000, Фамилия) = 12");
                Check(f != null && f.Table == "RAS_Таблица выбора фамилий" && f.Column == "Да/Нет" && f.Default == "1000" && f.Key == "Фамилия" && f.Number == 12, "разбор");
                Check(SignFormula.Make(f.Table, f.Column, f.Default, f.Key, 12) == "size_lookup(\"RAS_Таблица выбора фамилий\", \"Да/Нет\", 1000, Фамилия) = 12", "запись");
                Check(SignFormula.Tables("if(x, size_lookup(\"А\", \"Да/Нет\", 0, Ф) = 1, size_lookup(\"Б\", \"Да/Нет\", 0, Ф) = 2)").SequenceEqual(new[] { "А", "Б" }), "все таблицы формулы");
                var t = SizeTable.Parse(",Фамилия##other##,Да/Нет##number##general\r\n,,0\r\nИванов,Иванов,12\r\n\"Ким, А\",\"Ким, А\",13\r\n", "Фамилия", "Да/Нет");
                Check(t.KeyCol == 1 && t.ValueCol == 2 && t.NumberByKey()["Ким, А"] == 13 && t.KeyByNumber()[12] == "Иванов", "столбцы и строки");
                t.AddRow("Әбілов", 14);
                Check(t.Text(TableEncodings.Cp1251).Contains("Абилов,Абилов,14") && t.Text(TableEncodings.Utf8).Contains("Әбілов") && t.Text(TableEncodings.Utf8).Contains("\"Ким, А\""), "текст: 1251 — без казахских букв, кавычки");
            });
            yield return Case("кодировки таблицы", () =>
            {
                var text = ",Фамилия##other##\r\nӘбілов,1\r\n";
                Check(TableEncodings.Decode(TableEncodings.Encode(text, TableEncodings.Utf8Bom), out var e1) == text && e1 == TableEncodings.Utf8Bom, "UTF-8 с BOM");
                Check(TableEncodings.Decode(TableEncodings.Encode(text, TableEncodings.Utf8), out var e2) == text && e2 == TableEncodings.Utf8, "UTF-8");
                Check(TableEncodings.Decode(TableEncodings.Encode("Иванов", TableEncodings.Cp1251), out var e3) == "Иванов" && e3 == TableEncodings.Cp1251, "Windows-1251");
                Check(TableEncodings.Order(true, TableEncodings.Cp1251, true, true)[0] == TableEncodings.Utf8Bom && TableEncodings.Order(false, TableEncodings.Cp1251, true, true)[0] == TableEncodings.Cp1251, "порядок");
            });
        }

        private static SignFamilyState Family()
        {
            var f = new SignFamilyState { Rows = SizeTable.Parse(",Фамилия##other##,Да/Нет##number##general\r\n,,0\r\nИванов,Иванов,12\r\nПетров,Петров,13\r\nАбилов,Абилов,20\r\n", "Фамилия", "Да/Нет") };
            string F(int n, string table = "RAS_Таблица выбора фамилий") => SignFormula.Make(table, "Да/Нет", "1000", "Фамилия", n);
            f.Params["Фамилия"] = new SignParam { IsYesNo = false };
            f.Params["Иванов"] = new SignParam { IsYesNo = true, Formula = F(12) };
            f.Params["Петров"] = new SignParam { IsYesNo = true, Formula = F(13) };
            f.Params["Сидоров"] = new SignParam { IsYesNo = true, Formula = "Фамилия = \"Сидоров\"" };
            f.Params["Ким"] = new SignParam { IsYesNo = true, Formula = F(7, "Старая таблица") };
            f.Params["Ли"] = new SignParam { IsYesNo = false };
            f.Params["Чужой"] = new SignParam { IsYesNo = true, Formula = F(12) };
            f.WithDwg.Add("Иванов");
            f.WithDwg.Add("Сидоров");
            f.DwgFiles.Add("ким_clean.dwg");
            return f;
        }

        public static IEnumerable<KeyValuePair<string, Action>> Plan()
        {
            yield return Case("статусы и отметки", () =>
            {
                var plan = SignPlan.Make(new[]
                {
                    ("Иванов", "иванов_clean.dwg", (string)null),
                    ("Петров", "петров_clean.dwg", null),
                    ("Новиков", "новиков_clean.dwg", null),
                    ("Абилов", "абилов_clean.dwg", null),
                    ("Сидоров", "сидоров_clean.dwg", null),
                    ("Ким", "ким_clean.dwg", null),
                    ("Ли", "ли_clean.dwg", null),
                    ("Чужой", "чужой_clean.dwg", null),
                    ("Новиков", "новиков2_clean.dwg", null),
                    ("Әбілов", "әбілов_clean.dwg", null),
                    ("Фамилия", "ф_clean.dwg", null),
                }, Family()).ToDictionary(p => p.FileName);
                Check(plan["иванов_clean.dwg"].Action == SignAction.Replace && !plan["иванов_clean.dwg"].Checked, "уже добавлена — не отмечена");
                Check(plan["петров_clean.dwg"].Action == SignAction.Import && plan["петров_clean.dwg"].Checked && plan["петров_clean.dwg"].Number == 13, "параметр есть, подписи нет");
                Check(plan["новиков_clean.dwg"].Action == SignAction.New && plan["новиков_clean.dwg"].Number == null && plan["новиков_clean.dwg"].AddRow, "новая");
                Check(plan["абилов_clean.dwg"].Action == SignAction.New && plan["абилов_clean.dwg"].Number == 20 && !plan["абилов_clean.dwg"].AddRow, "новая, строка уже была");
                var sid = plan["сидоров_clean.dwg"];
                Check(sid.Action == SignAction.Fix && !sid.Checked && sid.HasDwg && !sid.InsertDwg, "формула другая, подпись стоит — второй DWG не вставляется");
                var kim = plan["ким_clean.dwg"];
                Check(kim.Action == SignAction.Fix && kim.Checked && kim.HasDwg && !kim.InsertDwg, "формула на другую таблицу, DWG по имени файла — только формула");
                Check(plan["ли_clean.dwg"].Action == SignAction.Skip, "параметр не Да/Нет");
                Check(plan["чужой_clean.dwg"].Action == SignAction.Skip && plan["чужой_clean.dwg"].Status.Contains("Иванов"), "номер занят другим");
                Check(plan["новиков2_clean.dwg"].Action == SignAction.Skip, "дубль");
                Check(plan["әбілов_clean.dwg"].Action == SignAction.Skip && plan["әбілов_clean.dwg"].Status.Contains("Абилов"), "казахская сливается с русской");
                Check(plan["ф_clean.dwg"].Action == SignAction.Skip, "служебное имя");
            });
            yield return Case("номера — только отмеченным, подряд, без дыр", () =>
            {
                var f = Family();
                var plan = SignPlan.Make(new[] { ("А", "а.dwg", (string)null), ("Б", "б.dwg", null), ("В", "в.dwg", null) }, f);
                SignPlan.Number(new[] { plan[0], plan[2] }, f);
                Check(plan[0].Number == 21 && plan[2].Number == 22 && plan[1].Number == null, "21, 22; неотмеченной — нет: " + plan[0].Number + ", " + plan[2].Number);
            });
        }

        public static IEnumerable<KeyValuePair<string, Action>> Settings()
        {
            yield return Case("настройки: по умолчанию, запись и чтение, ГИП, испорченное", () =>
            {
                var s = new SignSettings();
                Check(s.Height == 7 && s.MaxWidth == 22 && s.Tolerance == 0.02 && SignSettings.Rgb(s.Color).SequenceEqual(new byte[] { 0, 0, 255 }), "по умолчанию — как dwg_clean");
                s.Height = 9; s.Table = "Т";
                var back = SignSettings.FromJson(s.ToJson());
                Check(back.Height == 9 && back.Table == "Т" && back.KeyParam == "Фамилия", "туда и обратно");
                Check(SignSettings.FromJson("{\"height\": -3, \"color\": \"синий\"}").Height == 7 && SignSettings.FromJson("{\"color\": \"x\"}").Color == "0,0,255", "вне пределов — по умолчанию");
                Check(SignSettings.FromJson("{ испорчен") == null, "не разобрать — null");
                Check(new SignSettings().TableFor("RAS_Фамилия_ГИП") == "RAS_Таблица выбора ГИП" && new SignSettings().TableFor("RAS_Фамилия") == "RAS_Таблица выбора фамилий", "таблица ГИПов");
            });
        }
    }
}
