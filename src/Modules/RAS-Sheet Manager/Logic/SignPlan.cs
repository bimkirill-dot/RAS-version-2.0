using System;
using System.Collections.Generic;
using System.Linq;

namespace RASv2.SheetManager.Logic
{
    /// <summary>Что сделать с подписью.</summary>
    public enum SignAction
    {
        /// <summary>Нельзя: причина — в Status.</summary>
        Skip,
        /// <summary>Нового человека: строка таблицы (если нет), параметр Да/Нет с формулой, DWG.</summary>
        New,
        /// <summary>Параметр есть, подписи нет: только DWG.</summary>
        Import,
        /// <summary>Подпись уже есть: отмечена — заменить DWG.</summary>
        Replace,
        /// <summary>Параметр есть, формула другая (или на другую таблицу): переписать формулу; DWG — только если его нет.</summary>
        Fix,
    }

    /// <summary>Параметр семейства — что о нём нужно плану.</summary>
    public sealed class SignParam
    {
        public bool IsYesNo;
        public string Formula;
    }

    /// <summary>
    /// Форма формулы подписи: из какого столбца таблицы номер, по какому параметру (ключу) ищется фамилия, что — если фамилии
    /// нет. Берётся из формул самого семейства (у семейства ГИП столбец свой), в окне её можно поменять.
    /// </summary>
    public sealed class SignForm
    {
        public string Column = "Да/Нет", Default = "1000", Key = "Фамилия";
        /// <summary>Взята из формул семейства (а не из настроек).</summary>
        public bool FromFamily;
        /// <summary>Из настроек для семейства ГИП.</summary>
        public bool Gip;

        public string Text => "номер — из столбца «" + Column + "», по параметру «" + Key + "», если фамилии нет — " + Default;
    }

    /// <summary>Семейство подписей — как оно есть сейчас (читает Revit-часть модуля, план — без Revit API).</summary>
    public sealed class SignFamilyState
    {
        public string Table = "RAS_Таблица выбора фамилий";
        public string Column = "Да/Нет";
        public string Default = "1000";
        public string Key = "Фамилия";
        public SizeTable Rows = SizeTable.Create("Фамилия", "Да/Нет");
        public Dictionary<string, SignParam> Params = new Dictionary<string, SignParam>();
        /// <summary>Параметры, к которым уже привязана видимость какого-то DWG.</summary>
        public HashSet<string> WithDwg = new HashSet<string>();
        /// <summary>Имена файлов уже вставленных DWG (в нижнем регистре, как имя категории импорта).</summary>
        public HashSet<string> DwgFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Строка списка в окне: подпись из папки и что с ней будет.</summary>
    public sealed class SignPlanItem
    {
        public string Name;
        public string FileName;
        public string Note;
        public SignAction Action;
        public string Status;
        /// <summary>Отметка по умолчанию; человек может поменять (у Skip — нельзя).</summary>
        public bool Checked;
        /// <summary>Подпись уже есть в семействе (DWG привязан или вставлен этот файл).</summary>
        public bool HasDwg;
        /// <summary>Номер известен (из формулы или из таблицы); null — дать новый при добавлении.</summary>
        public int? Number;
        /// <summary>Строку в таблице добавить.</summary>
        public bool AddRow;

        /// <summary>Вставить DWG: новому, «нет подписи», замене; формуле — только если подписи нет (иначе была бы вторая поверх).</summary>
        public bool InsertDwg => Action == SignAction.New || Action == SignAction.Import || Action == SignAction.Replace || (Action == SignAction.Fix && !HasDwg);
    }

    /// <summary>
    /// План добавления подписей — перенос «Добавить подписи» (статусы, отметки) с исправлениями (разработчик, 27.10.2026):
    /// «формула другая» при уже стоящей подписи не вставляет второй DWG; новые номера даются только отмеченным, подряд,
    /// без дыр (Number). Без Revit API — проверяется тестами.
    /// </summary>
    public static class SignPlan
    {
        public static List<SignPlanItem> Make(IEnumerable<(string Name, string FileName, string Note)> signs, SignFamilyState f)
        {
            var byKey = f.Rows.NumberByKey();
            var byNumber = f.Rows.KeyByNumber();
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var list = new List<SignPlanItem>();
            foreach (var (name, file, note) in signs)
            {
                var it = new SignPlanItem { Name = SignNames.Clean(name), FileName = file, Note = note };
                list.Add(it);
                var nm = it.Name;
                var problem = SignNames.Problem(nm);
                if (problem != null) { Skip(it, problem); continue; }
                if (nm == f.Key || nm == f.Table || nm == f.Column) { Skip(it, "имя совпадает со служебным параметром"); continue; }
                var russian = SignNames.ToRussian(nm);
                if (seen.TryGetValue(russian, out var other))
                {
                    Skip(it, other == nm
                        ? "однофамилец «" + other + "» выше в списке — по регламенту добавьте обоим инициал через пробел («" + nm + " И»)"
                        : "совпадает с «" + other + "» без казахских букв — таблица выбора их не различит");
                    continue;
                }
                seen[russian] = nm;

                it.HasDwg = f.WithDwg.Contains(nm) || (file != null && f.DwgFiles.Contains(file));
                var rowNumber = byKey.TryGetValue(nm, out var n1) ? n1 : byKey.TryGetValue(russian, out var n2) ? n2 : (int?)null;
                if (f.Params.TryGetValue(nm, out var p))
                {
                    if (!p.IsYesNo) { Skip(it, "параметр «" + nm + "» уже есть, но он не Да/Нет"); continue; }
                    var formula = SignFormula.Parse(p.Formula);
                    if (formula != null && formula.Table == f.Table)
                    {
                        it.Number = formula.Number;
                        if (byNumber.TryGetValue(formula.Number, out var owner) && owner != nm && owner != russian)
                        {
                            Skip(it, "номер " + formula.Number + " в таблице занят фамилией «" + owner + "»");
                            continue;
                        }
                        it.AddRow = rowNumber == null;
                        if (it.HasDwg) Set(it, SignAction.Replace, false, "уже добавлена — отметьте, чтобы заменить DWG");
                        else Set(it, SignAction.Import, true, "параметр есть, подписи нет");
                    }
                    else
                    {
                        it.Number = rowNumber;
                        it.AddRow = rowNumber == null;
                        if (formula != null)
                            Set(it, SignAction.Fix, true, "формула ссылается на таблицу «" + formula.Table + "» — перепишется на «" + f.Table + "»" + (it.HasDwg ? " (подпись уже стоит)" : ""));
                        else
                            Set(it, SignAction.Fix, false, "параметр есть, формула другая — отметьте, чтобы переписать" + (it.HasDwg ? " (подпись уже стоит)" : ""));
                    }
                }
                else
                {
                    it.Number = rowNumber;
                    it.AddRow = rowNumber == null;
                    Set(it, SignAction.New, true, rowNumber != null ? "новая (строка в таблице уже была)" : "новая");
                }
                if (note != null) it.Status += "; " + note;
            }
            return list;
        }

        private static void Skip(SignPlanItem it, string why) => Set(it, SignAction.Skip, false, why);

        private static void Set(SignPlanItem it, SignAction a, bool check, string status)
        {
            it.Action = a;
            it.Checked = check;
            it.Status = status;
        }

        /// <summary>
        /// Новые номера — только отмеченным, у кого номера нет, по порядку списка: от наибольшего занятого (в таблице и в формулах
        /// таблицы) + 1. Номера не переиспользуются.
        /// </summary>
        public static void Number(IEnumerable<SignPlanItem> chosen, SignFamilyState f)
        {
            var used = f.Rows.KeyByNumber().Keys
                .Concat(f.Params.Values.Select(p => SignFormula.Parse(p.Formula)).Where(x => x != null && x.Table == f.Table).Select(x => x.Number))
                .Where(n => n > 0).ToList();
            var next = used.Count > 0 ? used.Max() + 1 : 1;
            foreach (var it in chosen)
                if (it.Number == null) it.Number = next++;
        }
    }
}
