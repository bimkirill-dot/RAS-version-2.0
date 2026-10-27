using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RASv2.SheetManager.Logic;

namespace RASv2.SheetManager.Signing
{
    /// <summary>Таблица выбора в семействе: имя и сколько формул на неё ссылается.</summary>
    public sealed class SignTableChoice
    {
        public string Name;
        public int Formulas;
        /// <summary>Самая частая форма формул на эту таблицу: столбец, по умолчанию, ключ.</summary>
        public string Column, Default, Key;
    }

    /// <summary>Подпись, которую нужно добавить: строка плана и её очищенный файл.</summary>
    public sealed class SignJob
    {
        public SignPlanItem Item;
        /// <summary>Остальные столбцы новой строки таблицы (номер столбца → значение): у таблицы ГИП, например, свои.</summary>
        public Dictionary<int, string> Extra = new Dictionary<int, string>();
        public string CleanPath;
        public string Result;
        public bool Ok;
    }

    /// <summary>Что сделано — для отчёта.</summary>
    public sealed class SignReport
    {
        public string Table, Encoding, Check, Point, Scale;
        public int Painted;
        public List<string> RemovedTables = new List<string>();
        public string Backup;
        public string Error;
    }

    /// <summary>
    /// Семейство подписей в Revit — перенос «Добавить подписи» (AddSigns) с исправлениями (DECISIONS.md модуля, «Подпись»):
    /// прочитать, что в нём есть (таблицы выбора, параметры, вставленные DWG, общая точка подписей), и добавить отмеченные
    /// подписи одной группой действий (одно Ctrl+Z): таблица, человек целиком (параметр, формула, DWG — вместе или ничего),
    /// самопроверка таблицы, цвет, по галочке — удалить неиспользуемые таблицы. Только в контексте Revit API.
    /// </summary>
    public sealed class SignFamily
    {
        private const double MmPerFoot = 304.8;
        private readonly Document _doc;
        private readonly SignSettings _s;
        private readonly string _temp;

        public SignFamily(Document doc, SignSettings settings, string tempDir)
        {
            _doc = doc;
            _s = settings;
            _temp = tempDir;
        }

        public string Title => _doc.Title;

        private FamilyManager Fm => _doc.FamilyManager;

        private FamilySizeTableManager Tables => FamilySizeTableManager.GetFamilySizeTableManager(_doc, _doc.OwnerFamily.Id);

        /// <summary>Таблицы выбора: на которые ссылаются формулы (больше формул — выше) и остальные в семействе.</summary>
        public List<SignTableChoice> TableChoices()
        {
            var votes = new Dictionary<(string, string, string, string), int>();
            foreach (FamilyParameter p in Fm.Parameters)
            {
                var f = SignFormula.Parse(Formula(p));
                if (f == null || f.Number <= 0) continue;
                var k = (f.Table, f.Column, f.Default, f.Key);
                votes[k] = votes.TryGetValue(k, out var n) ? n + 1 : 1;
            }
            var list = votes.GroupBy(v => v.Key.Item1).Select(g =>
            {
                var best = g.OrderByDescending(v => v.Value).First().Key;
                return new SignTableChoice { Name = g.Key, Formulas = g.Sum(v => v.Value), Column = best.Item2, Default = best.Item3, Key = best.Item4 };
            }).OrderByDescending(t => t.Formulas).ToList();
            foreach (var name in AllTables().Where(n => list.All(t => t.Name != n)))
                list.Add(new SignTableChoice { Name = name });
            return list;
        }

        private List<string> AllTables()
        {
            try { return Tables?.GetAllSizeTableNames()?.ToList() ?? new List<string>(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return new List<string>(); }
        }

        /// <summary>Таблицы, на которые не ссылается ни одна формула (все size_lookup во всех формулах), кроме выбранной.</summary>
        public List<string> UnusedTables(string chosen)
        {
            var used = new HashSet<string>(Fm.Parameters.Cast<FamilyParameter>().SelectMany(p => SignFormula.Tables(Formula(p))));
            return AllTables().Where(t => !used.Contains(t) && t != chosen).ToList();
        }

        /// <summary>
        /// Состояние семейства для плана с этой таблицей: форма формулы — как у неё (новой — как у самой частой в семействе,
        /// иначе из настроек), её строки (выгрузка во временную папку — она же резервная копия), параметры, вставленные DWG.
        /// </summary>
        /// <summary>
        /// Форма формулы для таблицы — как у формул на неё в семействе (у семейства ГИП — своя); формул на неё нет — из настроек
        /// («Формула»: обычная или для семейства ГИП — по признаку в имени семейства), но столбец — только тот, что есть в
        /// таблице (нет — первый числовой столбец таблицы).
        /// </summary>
        public SignForm FormFor(string table, List<SignTableChoice> choices, SizeTable rows)
        {
            var c = choices.FirstOrDefault(x => x.Name == table && x.Key != null);
            if (c != null) return new SignForm { Column = c.Column, Default = c.Default, Key = c.Key, FromFamily = true };
            var form = _s.FormFor(_doc.Title);
            var columns = rows?.NumberColumns() ?? new List<string>();
            if (columns.Count > 0 && !columns.Contains(form.Column)) form.Column = columns[0];
            return form;
        }

        /// <summary>Текстовые параметры экземпляра семейства — куда вписывают фамилию (ключ таблицы).</summary>
        public List<string> TextParams()
        {
            var list = new List<string>();
            foreach (FamilyParameter p in Fm.Parameters)
            {
                try { if (p.IsInstance && p.Definition.GetDataType() == SpecTypeId.String.Text) list.Add(p.Definition.Name); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { /* параметр без типа — не ключ */ }
            }
            return list.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Строки таблицы как есть (выгрузка во временную папку); таблицы нет — null.</summary>
        public SizeTable ReadTable(string table, string key, string column, out string sourceEncoding, out string backup)
        {
            sourceEncoding = TableEncodings.Cp1251;
            backup = null;
            var mgr = Tables;
            if (mgr == null || !mgr.HasSizeTable(table)) return null;
            Directory.CreateDirectory(_temp);
            backup = Path.Combine(_temp, "исходная_" + Safe(table) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
            if (!mgr.ExportSizeTable(table, backup)) { backup = null; return null; }
            return SizeTable.Parse(TableEncodings.Decode(File.ReadAllBytes(backup), out sourceEncoding), key, column);
        }

        public SignFamilyState State(string table, SignForm form, out string sourceEncoding, out string backup)
        {
            var f = new SignFamilyState
            {
                Table = table,
                Column = form.Column,
                Default = form.Default,
                Key = form.Key,
            };
            f.Rows = ReadTable(table, f.Key, f.Column, out sourceEncoding, out backup) ?? SizeTable.Create(f.Key, f.Column);
            foreach (FamilyParameter p in Fm.Parameters)
                f.Params[p.Definition.Name] = new SignParam { IsYesNo = IsYesNo(p), Formula = Formula(p) };
            foreach (var imp in new FilteredElementCollector(_doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                var file = imp.Category?.Name;
                if (!string.IsNullOrEmpty(file)) f.DwgFiles.Add(file);
                var fp = Associated(imp);
                if (fp != null) f.WithDwg.Add(fp.Definition.Name);
            }
            return f;
        }

        /// <summary>Самая частая точка вставки уже стоящих подписей (с точностью 0,01 мм); подписей нет — null.</summary>
        public XYZ Anchor(out int spread)
        {
            var points = Signatures().Select(Origin).Where(o => o != null).ToList();
            spread = 0;
            if (points.Count == 0) return null;
            var groups = points.GroupBy(o => (Math.Round(o.X * MmPerFoot, 2), Math.Round(o.Y * MmPerFoot, 2))).OrderByDescending(g => g.Count()).ToList();
            spread = points.Count - groups[0].Count();
            return groups[0].First();
        }

        /// <summary>Подпись — DWG с привязанной к параметру видимостью или очищенный («…_clean»).</summary>
        private IEnumerable<ImportInstance> Signatures() =>
            new FilteredElementCollector(_doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(i => Associated(i) != null || Path.GetFileNameWithoutExtension(i.Category?.Name ?? "").EndsWith(SignFolder.Suffix, StringComparison.OrdinalIgnoreCase));

        private static XYZ Origin(ImportInstance i)
        {
            try { return i.GetTransform().Origin; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        private FamilyParameter Associated(Element e)
        {
            var vp = Visibility(e);
            try { return vp != null ? Fm.GetAssociatedFamilyParameter(vp) : null; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        private static Parameter Visibility(Element e) =>
            e.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM) ?? e.LookupParameter("Видимость") ?? e.LookupParameter("Visible");

        private static string Formula(FamilyParameter p)
        {
            try { return p.Formula; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        private static bool IsYesNo(FamilyParameter p)
        {
            try { return p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
        }

        private static string Safe(string name) => new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());

        // ───────────────────────────────── добавление ─────────────────────────────────

        /// <summary>
        /// Добавить подписи (jobs — отмеченные, с номерами). Группа «RAS: подписи» — одно Ctrl+Z; при неудаче — откат целиком,
        /// причина — в Error. point — куда поставить центр подписи (футы); scale — коэффициент (у ГИПов подпись крупнее).
        /// </summary>
        public SignReport Apply(View view, SignFamilyState state, string sourceEncoding, bool tableExisted, List<SignJob> jobs,
            XYZ point, double scale, bool removeUnused, string backup)
        {
            var report = new SignReport { Table = state.Table, Backup = backup, Scale = scale.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")) };
            report.Point = string.Format(CultureInfo.GetCultureInfo("ru-RU"), "X={0:0.0}, Y={1:0.0} мм", point.X * MmPerFoot, point.Y * MmPerFoot);
            var table = state.Rows;
            foreach (var j in jobs.Where(j => j.Item.AddRow && j.Item.Number != null))
                if (!table.NumberByKey().ContainsKey(j.Item.Name)) table.AddRow(j.Item.Name, j.Item.Number.Value, j.Extra);
            var order = TableEncodings.Order(table.HasKazakh, sourceEncoding, table.Text(TableEncodings.Utf8).Any(ch => ch > 127), tableExisted);

            using (var group = new TransactionGroup(_doc, "RAS: подписи"))
            {
                group.Start();
                try
                {
                    // 1. таблица, параметр фамилии, тип
                    FamilyParameter key = null;
                    Run("Подписи: таблица выбора", () =>
                    {
                        if (Tables == null) FamilySizeTableManager.CreateFamilySizeTableManager(_doc, _doc.OwnerFamily.Id);
                        key = Fm.get_Parameter(state.Key) ?? Fm.AddParameter(state.Key, GroupTypeId.Text, SpecTypeId.String.Text, true);
                        if (Fm.CurrentType == null) Fm.NewType("Тип 1");
                        // не загрузилась в одной кодировке — следующая (а не отмена всего)
                        string why = null;
                        foreach (var enc in order)
                        {
                            try
                            {
                                ImportTable(table, enc, state.Table);
                                report.Encoding = enc;
                                return;
                            }
                            catch (InvalidOperationException ex) { why = ex.Message; }
                        }
                        throw new InvalidOperationException(why ?? "таблица выбора не загрузилась");
                    });
                    order = order.SkipWhile(e => e != report.Encoding).ToList();

                    // 2. масштаб — способ, который этот Revit понимает (проверка пробной вставкой)
                    var options = ImportOptions(scale, view, jobs.FirstOrDefault(j => j.Item.InsertDwg)?.CleanPath);
                    var offset = new XYZ(point.X, point.Y, 0);

                    // 3. каждый человек — целиком: параметр, формула, DWG; сбой — откат только его
                    foreach (var j in jobs) Person(j, state, options, view, offset);

                    // 4. самопроверка: вписать фамилию — её параметр должен стать «Да»; нет — другая кодировка
                    var test = TestNames(jobs);
                    if (test.Count > 0 && key != null)
                    {
                        var ok = Works(test, key, order[0]);
                        foreach (var enc in order.Skip(1))
                        {
                            if (ok) break;
                            try
                            {
                                Run("Подписи: таблица выбора (кодировка)", () => ImportTable(table, enc, state.Table));
                                report.Encoding = enc;
                                ok = Works(test, key, enc);
                            }
                            catch (Exception ex) when (ex is InvalidOperationException || ex is Autodesk.Revit.Exceptions.ApplicationException) { /* следующая кодировка */ }
                        }
                        if (!ok)
                            throw new InvalidOperationException("Revit не находит фамилии в таблице выбора «" + state.Table + "» ни в одной кодировке — ничего не добавлено. Проверьте таблицу вручную.");
                        report.Check = "при «" + state.Key + " = " + string.Join("» и «", test.Select(p => p.Name)) + "» подпись включается";
                    }
                    else report.Check = "не проверялось — новых параметров нет";

                    // 5. цвет — у всех подписей семейства (стили объектов: категория импорта и её слои)
                    Run("Подписи: цвет", () => report.Painted = Paint());

                    // 6. по галочке — таблицы, на которые не ссылается ни одна формула
                    if (removeUnused)
                        Run("Подписи: неиспользуемые таблицы", () =>
                        {
                            foreach (var t in UnusedTables(state.Table))
                                if (Tables.RemoveSizeTable(t)) report.RemovedTables.Add(t);
                        });

                    group.Assimilate();
                }
                catch (Exception ex)
                {
                    if (group.HasStarted()) group.RollBack();
                    report.Error = ex.Message;
                    foreach (var j in jobs) { j.Ok = false; j.Result = "не добавлена — отменено целиком"; }
                }
            }
            return report;
        }

        private void Person(SignJob j, SignFamilyState state, DWGImportOptions options, View view, XYZ offset)
        {
            var it = j.Item;
            using (var t = Start("Подпись: " + it.Name))
            {
                try
                {
                    var fp = Fm.get_Parameter(it.Name);
                    var formula = SignFormula.Make(state.Table, state.Column, state.Default, state.Key, it.Number ?? 0);
                    if (it.Action == SignAction.New)
                    {
                        fp = Fm.AddParameter(it.Name, GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, true);
                        Fm.SetFormula(fp, formula);
                    }
                    else if (it.Action == SignAction.Fix) Fm.SetFormula(fp, formula);
                    if (fp == null) throw new InvalidOperationException("нет параметра «" + it.Name + "»");

                    if (it.InsertDwg)
                    {
                        if (it.Action == SignAction.Replace)
                        {
                            var file = Path.GetFileName(j.CleanPath);
                            var olds = new FilteredElementCollector(_doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                                .Where(i => Associated(i)?.Definition.Name == it.Name || string.Equals(i.Category?.Name, file, StringComparison.OrdinalIgnoreCase))
                                .Select(i => { if (i.Pinned) i.Pinned = false; return i.Id; }).ToList();
                            if (olds.Count > 0) _doc.Delete(olds);
                        }
                        if (!_doc.Import(j.CleanPath, options, view, out var id) || id == null || id == ElementId.InvalidElementId)
                            throw new InvalidOperationException("Revit не вставил файл");
                        var imp = _doc.GetElement(id);
                        // вставку «по началу координат» Revit сразу закрепляет — открепить на время сдвига
                        if (offset.GetLength() > 1e-9)
                        {
                            imp.Pinned = false;
                            ElementTransformUtils.MoveElement(_doc, id, offset);
                        }
                        var vp = Visibility(imp);
                        if (vp == null || !Fm.CanElementParameterBeAssociated(vp)) throw new InvalidOperationException("у вставки нет параметра «Видимость»");
                        Fm.AssociateElementParameterToFamilyParameter(vp, fp);
                        imp.Pinned = true;
                    }
                    t.Commit();
                    j.Ok = true;
                    j.Result = it.Action == SignAction.Replace ? "заменена" : it.InsertDwg ? "добавлена" : "формула переписана";
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    j.Ok = false;
                    j.Result = "ошибка: " + ex.Message;
                }
            }
        }

        /// <summary>Одна казахская и одна русская фамилия из добавленных с новым параметром или формулой.</summary>
        private List<SignPlanItem> TestNames(List<SignJob> jobs)
        {
            var have = jobs.Where(j => j.Ok && (j.Item.Action == SignAction.New || j.Item.Action == SignAction.Fix)).Select(j => j.Item).ToList();
            var kz = have.LastOrDefault(i => SignNames.HasKazakh(i.Name));
            var ru = have.LastOrDefault(i => !SignNames.HasKazakh(i.Name));
            return new[] { kz, ru }.Where(i => i != null).ToList();
        }

        private bool Works(List<SignPlanItem> names, FamilyParameter key, string encoding)
        {
            using (var t = Start("Подписи: проверка"))
            {
                try
                {
                    foreach (var it in names)
                    {
                        Fm.Set(key, encoding == TableEncodings.Cp1251 ? SignNames.ToRussian(it.Name) : it.Name);
                        _doc.Regenerate();
                        var p = Fm.get_Parameter(it.Name);
                        if (p == null || Fm.CurrentType.AsInteger(p) != 1) return false;
                    }
                    return true;
                }
                finally
                {
                    t.RollBack();
                }
            }
        }

        private void ImportTable(SizeTable table, string encoding, string name)
        {
            Directory.CreateDirectory(_temp);
            // имя файла = имя таблицы в семействе
            var path = Path.Combine(_temp, name + ".csv");
            File.WriteAllBytes(path, TableEncodings.Encode(table.Text(encoding), encoding));
            var err = new FamilySizeTableErrorInfo();
            if (!FamilySizeTableManager.GetFamilySizeTableManager(_doc, _doc.OwnerFamily.Id).ImportSizeTable(_doc, path, err))
                throw new InvalidOperationException("таблица выбора не загрузилась: " + err.FamilySizeTableErrorType + ", строка " + err.InvalidRowIndex + ", столбец " + err.InvalidColumnIndex);
        }

        /// <summary>
        /// Параметры вставки DWG: центр подписи — в начало координат, мм, только этот вид, цвета как в файле. Масштаб ≠ 1 Revit
        /// в разных версиях понимает по-разному — способ подбирается пробной вставкой (откат) и проверяется по ширине (2 %).
        /// </summary>
        private DWGImportOptions ImportOptions(double scale, View view, string probe)
        {
            DWGImportOptions Make(ImportUnit unit, double? custom)
            {
                var o = new DWGImportOptions
                {
                    Placement = ImportPlacement.Origin,
                    Unit = unit,
                    ThisViewOnly = true,
                    ColorMode = ImportColorMode.Preserved,
                    OrientToView = true,
                    VisibleLayersOnly = true,
                    AutoCorrectAlmostVHLines = false,
                };
                if (custom != null) o.CustomScale = custom.Value;
                return o;
            }
            var mm = Make(ImportUnit.Millimeter, null);
            if (Math.Abs(scale - 1) < 1e-9 || probe == null) return mm;
            var w1 = Width(mm, view, probe);
            bool Close(double? w) => w1 != null && w != null && Math.Abs(w.Value / w1.Value - scale) / scale < 0.02;
            var a = Make(ImportUnit.Millimeter, scale);
            if (Close(Width(a, view, probe))) return a;
            var r1 = Width(Make(ImportUnit.Default, 1), view, probe);
            if (w1 != null && r1 != null)
            {
                var b = Make(ImportUnit.Default, scale * w1.Value / r1.Value);
                if (Close(Width(b, view, probe))) return b;
            }
            throw new InvalidOperationException("Revit не применил масштаб " + scale.ToString(CultureInfo.GetCultureInfo("ru-RU")) + " к пробной вставке — ничего не добавлено.");
        }

        /// <summary>Пробная вставка (откатывается): ширина в футах.</summary>
        private double? Width(DWGImportOptions o, View view, string path)
        {
            using (var t = Start("Подписи: проба масштаба"))
            {
                try
                {
                    if (!_doc.Import(path, o, view, out var id)) return null;
                    var bb = _doc.GetElement(id)?.get_BoundingBox(null);
                    return bb != null ? bb.Max.X - bb.Min.X : (double?)null;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
                finally { t.RollBack(); }
            }
        }

        /// <summary>Цвет подписей — у категории импорта и её слоёв (как «Стили объектов» → «Импортированные категории»).</summary>
        private int Paint()
        {
            var rgb = SignSettings.SignColor;
            var color = new Color(rgb[0], rgb[1], rgb[2]);
            var n = 0;
            foreach (var imp in Signatures())
            {
                var cat = imp.Category;
                if (cat == null) continue;
                var changed = false;
                foreach (var c in new[] { cat }.Concat(cat.SubCategories.Cast<Category>()))
                {
                    var lc = c.LineColor;
                    if (lc != null && lc.IsValid && lc.Red == color.Red && lc.Green == color.Green && lc.Blue == color.Blue) continue;
                    c.LineColor = color;
                    changed = true;
                }
                if (changed) n++;
            }
            return n;
        }

        private void Run(string name, Action action)
        {
            using (var t = Start(name))
            {
                try
                {
                    action();
                    t.Commit();
                }
                catch
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    throw;
                }
            }
        }

        /// <summary>Транзакция без окон предупреждений: пакет не останавливается на каждом (ошибки — как обычно).</summary>
        private Transaction Start(string name)
        {
            var t = new Transaction(_doc, name);
            var o = t.GetFailureHandlingOptions();
            o.SetFailuresPreprocessor(new NoWarnings());
            o.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(o);
            t.Start();
            return t;
        }

        // ───────────────────────────────── актуализация ─────────────────────────────────

        /// <summary>
        /// Подписи семейства, которые пора убрать (разработчик, 27.10.2026, «Актуализировать»): человек (параметр Да/Нет с
        /// формулой на выбранную таблицу и номером больше нуля, или с привязанным DWG; служебные параметры вроде «Ошибка в
        /// фамилии» с номером -1 — не люди, 27.10.2026 попал в список), которого нет в папке подписей (people — фамилии из папки; при совпадении
        /// без учёта казахских букв — тоже есть), или чей DWG не очищен (вставлен вручную: имя файла — не «…_clean»).
        /// </summary>
        public List<SignStale> Stale(SignFamilyState state, ICollection<string> people)
        {
            var inFolder = new HashSet<string>(people.Select(SignNames.ToRussian));
            var dwg = new Dictionary<string, List<ImportInstance>>();
            foreach (var imp in new FilteredElementCollector(_doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                var fp = Associated(imp);
                if (fp == null) continue;
                if (!dwg.TryGetValue(fp.Definition.Name, out var list)) dwg[fp.Definition.Name] = list = new List<ImportInstance>();
                list.Add(imp);
            }
            var result = new List<SignStale>();
            foreach (FamilyParameter p in Fm.Parameters)
            {
                var name = p.Definition.Name;
                var formula = SignFormula.Parse(Formula(p));
                // человек — параметр Да/Нет с номером больше нуля; служебные («Ошибка в фамилии» = -1 и т. п.) — не трогать
                if (!IsYesNo(p)) continue;
                var mine = formula != null && formula.Table == state.Table && formula.Number > 0;
                dwg.TryGetValue(name, out var imports);
                if (!mine && imports == null) continue;
                if (formula != null && formula.Number <= 0) continue;
                if (name == state.Key) continue;
                string why = null;
                if (!inFolder.Contains(SignNames.ToRussian(name))) why = "файла подписи нет в папке";
                else if (imports != null && imports.Any(i => !Path.GetFileNameWithoutExtension(i.Category?.Name ?? "").EndsWith(SignFolder.Suffix, StringComparison.OrdinalIgnoreCase)))
                    why = "подпись вставлена вручную — не очищена";
                if (why == null) continue;
                result.Add(new SignStale { Name = name, Reason = why, Number = formula?.Number, Dwg = (imports ?? new List<ImportInstance>()).Select(i => i.Category?.Name ?? "").ToList() });
            }
            return result.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// Убрать подписи из семейства — одной группой (одно Ctrl+Z): DWG и их типы (вместе с категорией импорта), параметры,
        /// строки таблицы (таблица загружается заново в той же кодировке). null — сделано, иначе — почему нет (откат целиком).
        /// </summary>
        public string Remove(SignFamilyState state, string sourceEncoding, List<SignStale> stale)
        {
            using (var group = new TransactionGroup(_doc, "RAS: подписи — актуализация"))
            {
                group.Start();
                try
                {
                    Run("Подписи: удалить неактуальные", () =>
                    {
                        var names = new HashSet<string>(stale.Select(x => x.Name));
                        var imports = new FilteredElementCollector(_doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                            .Where(i => { var fp = Associated(i); return fp != null && names.Contains(fp.Definition.Name); }).ToList();
                        var types = imports.Select(i => i.GetTypeId()).Where(id => id != null && id != ElementId.InvalidElementId).Distinct().ToList();
                        foreach (var i in imports) if (i.Pinned) i.Pinned = false;
                        if (imports.Count > 0) _doc.Delete(imports.Select(i => i.Id).ToList());
                        // тип вставки (CAD) — вместе с ним уходит и категория импорта со слоями
                        var leftTypes = types.Where(id => _doc.GetElement(id) != null).ToList();
                        if (leftTypes.Count > 0) _doc.Delete(leftTypes);
                        foreach (var n in names)
                        {
                            var p = Fm.get_Parameter(n);
                            if (p != null) Fm.RemoveParameter(p);
                        }
                        if (state.Rows.RemoveRows(names) > 0 && Tables != null && Tables.HasSizeTable(state.Table))
                            ImportTable(state.Rows, sourceEncoding, state.Table);
                    });
                    group.Assimilate();
                    return null;
                }
                catch (Exception ex)
                {
                    if (group.HasStarted()) group.RollBack();
                    return ex.Message;
                }
            }
        }

        private sealed class NoWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var f in fa.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) fa.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }
    }
}
