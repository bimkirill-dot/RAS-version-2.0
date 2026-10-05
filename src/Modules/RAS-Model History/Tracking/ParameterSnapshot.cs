using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// «Фотография» значений параметров элемента. Revit не сообщает старые значения,
    /// поэтому снимок делается заранее (при выделении, активации вида, при открытии,
    /// после каждого изменения), а при следующем изменении сравнивается с новым состоянием.
    /// </summary>
    public sealed class ParameterSnapshot
    {
        private const int MaxValueLength = 300;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// «Редактирует» — кто занял элемент в совместной работе. Меняется, когда элемент просто взяли для правки,
        /// поэтому не снимается и не сравнивается (в кэше снимков прошлых сеансов он ещё может быть).
        /// </summary>
        private static readonly string EditedByKey = "i:" + ((long)BuiltInParameter.EDITED_BY).ToString(Inv);

        /// <summary>ключ параметра → (отображаемое имя, значение)</summary>
        public Dictionary<string, KeyValuePair<string, string>> Values { get; } =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);

        public static ParameterSnapshot Capture(Document doc, Element el, string typeName, DocumentTracker t = null)
        {
            var snap = new ParameterSnapshot();

            IEnumerable<Parameter> parameters = null;
            try { parameters = el.GetOrderedParameters(); } catch { /* ignore */ }
            if (parameters == null)
            {
                try { parameters = el.Parameters.Cast<Parameter>(); } catch { parameters = Enumerable.Empty<Parameter>(); }
            }

            foreach (var p in parameters)
            {
                try
                {
                    if (p == null || p.StorageType == StorageType.None) continue;
                    var name = p.Definition?.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    string key = p.IsShared ? "s:" + p.GUID.ToString("N") : "i:" + p.Id.Value.ToString(Inv);
                    if (key == EditedByKey) continue;
                    snap.Values[string.Intern(key)] = new KeyValuePair<string, string>(string.Intern(name), Pool(t, FormatValue(doc, p)));
                }
                catch { /* отдельный параметр может быть недоступен */ }
            }

            // Псевдо-параметры: то, что меняется, но не является параметром
            snap.Add("~name", "[Имя]", ElementDescriber.GetName(el));
            if (!string.IsNullOrEmpty(typeName)) snap.Add("~type", "[Тип]", typeName);
            AddLocation(snap, el);
            if (el is View view) AddViewGraphics(snap, doc, view, t?.Categories);
            return snap;
        }

        /// <summary>Короткие значения («Да», «0», имена типов) часто повторяются — храним один экземпляр.</summary>
        private static string Pool(DocumentTracker t, string value)
        {
            if (t == null || string.IsNullOrEmpty(value) || value.Length > 64) return value;
            return t.Pool(value);
        }

        private void Add(string key, string name, string value)
        {
            Values[key] = new KeyValuePair<string, string>(name, Trim(value));
        }

        public static string FormatValue(Document doc, Parameter p)
        {
            try
            {
                if (!p.HasValue) return "";
                switch (p.StorageType)
                {
                    case StorageType.String:
                        return Trim(p.AsString() ?? "");
                    case StorageType.Integer:
                        return Trim(p.AsValueString() ?? p.AsInteger().ToString(Inv));
                    case StorageType.Double:
                        return Trim(p.AsValueString() ?? p.AsDouble().ToString("G6", Inv));
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        if (!ElementDescriber.IsValid(id)) return "<нет>";
                        var vs = p.AsValueString();
                        if (!string.IsNullOrEmpty(vs)) return Trim(vs);
                        var target = id.Value > 0 ? doc.GetElement(id) : null;
                        return Trim(target != null ? ElementDescriber.GetName(target) : id.Value.ToString(Inv));
                }
            }
            catch { /* ignore */ }
            return "";
        }

        private static void AddLocation(ParameterSnapshot snap, Element el)
        {
            try
            {
                switch (el.Location)
                {
                    case LocationPoint lp:
                        snap.Add("~loc", "[Положение, мм]", FormatXyz(lp.Point));
                        try
                        {
                            var deg = lp.Rotation * 180.0 / Math.PI;
                            snap.Add("~rot", "[Поворот, °]", deg.ToString("0.##", Inv));
                        }
                        catch { /* у некоторых элементов поворот недоступен */ }
                        break;
                    case LocationCurve lc:
                        var c = lc.Curve;
                        snap.Add("~loc", "[Положение, мм]", FormatXyz(c.GetEndPoint(0)) + " → " + FormatXyz(c.GetEndPoint(1)));
                        break;
                    default:
                        // ни точки вставки, ни оси (арматура, перекрытия, крыши, лестницы, ограждения): без габаритов
                        // сдвиг не менял бы ни одного значения и выглядел бы как «изменён без отличий»
                        if (el is ElementType || el is View || el.ViewSpecific || el.Category?.CategoryType != CategoryType.Model) break;
                        var box = el.get_BoundingBox(null);
                        if (box != null && box.Enabled)
                            snap.Add(BoxKey, "[Габариты, мм]", FormatXyz(box.Min, 0) + " → " + FormatXyz(box.Max, 0));
                        break;
                }
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Габариты элементов без точки вставки и оси. В снимках прежних версий их нет — тогда не сравниваются,
        /// иначе первая же правка каждого такого элемента показала бы «габариты: → …».
        /// </summary>
        private const string BoxKey = "~box";

        /// <param name="digits">Знаков после запятой: габариты — целые мм (их пересчёт Revit даёт дрожание в долях мм).</param>
        private static string FormatXyz(XYZ p, int digits = 1)
        {
            return "(" + Mm(p.X, digits) + "; " + Mm(p.Y, digits) + "; " + Mm(p.Z, digits) + ")";
        }

        private static string Mm(double feet, int digits = 1)
        {
            var mm = UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
            return Math.Round(mm, digits).ToString(digits == 0 ? "0" : "0.#", Inv);
        }

        /// <summary>Фильтры вида и скрытые категории — изменения «Видимость/Графика».</summary>
        private static void AddViewGraphics(ParameterSnapshot snap, Document doc, View view, List<Category> categories)
        {
            try
            {
                var filters = new List<string>();
                foreach (var fid in view.GetFilters())
                {
                    var f = doc.GetElement(fid);
                    if (f == null) continue;
                    bool visible = true;
                    try { visible = view.GetFilterVisibility(fid); } catch { /* ignore */ }
                    filters.Add(f.Name + (visible ? "" : " (скрыт)"));
                }
                filters.Sort(StringComparer.CurrentCulture);
                snap.Add("~filters", "[Фильтры вида]", filters.Count == 0 ? "—" : string.Join(", ", filters));
            }
            catch { /* у листов/спецификаций фильтров нет */ }

            try
            {
                var hidden = new List<string>();
                bool anyControllable = false;
                IEnumerable categoriesSource = (IEnumerable)categories ?? doc.Settings.Categories;
                foreach (Category cat in categoriesSource)
                {
                    try
                    {
                        if (cat == null || !cat.get_AllowsVisibilityControl(view)) continue;
                        anyControllable = true;
                        if (view.GetCategoryHidden(cat.Id)) hidden.Add(cat.Name);
                    }
                    catch { /* ignore */ }
                }
                if (anyControllable)
                {
                    hidden.Sort(StringComparer.CurrentCulture);
                    snap.Values["~hidden"] = new KeyValuePair<string, string>("[Скрытые категории]",
                        hidden.Count == 0 ? "—" : Trim(string.Join(", ", hidden), 2000));
                }
            }
            catch { /* ignore */ }
        }

        /// <summary>Значение параметра по имени (без учёта регистра); null — параметра нет.</summary>
        public string ValueByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var kv in Values)
            {
                if (string.Equals(kv.Value.Key, name, StringComparison.CurrentCultureIgnoreCase)) return kv.Value.Value;
            }
            return null;
        }

        /// <summary>Все непустые параметры — для записи в журнал (удаление / создание).</summary>
        public List<ParamValue> ToState()
        {
            var list = new List<ParamValue>(Values.Count);
            foreach (var kv in Values)
            {
                if (kv.Key == "~hidden") continue; // длинный список скрытых категорий в журнал не пишем
                if (string.IsNullOrEmpty(kv.Value.Value)) continue;
                list.Add(new ParamValue { Name = kv.Value.Key, Value = kv.Value.Value });
            }
            return list;
        }

        /// <summary>Сравнить со следующим снимком того же элемента.</summary>
        public List<ParamChange> DiffTo(ParameterSnapshot after)
        {
            var result = new List<ParamChange>();
            // габариты сравниваются, только если они есть в обоих снимках (см. BoxKey)
            var compareBox = Values.ContainsKey(BoxKey) && after.Values.ContainsKey(BoxKey);
            foreach (var kv in after.Values)
            {
                if (kv.Key == EditedByKey) continue;
                if (kv.Key == BoxKey && !compareBox) continue;
                if (Values.TryGetValue(kv.Key, out var old))
                {
                    if (!string.Equals(old.Value, kv.Value.Value, StringComparison.Ordinal))
                        result.Add(new ParamChange { Name = kv.Value.Key, Old = old.Value, New = kv.Value.Value });
                }
                else if (!string.IsNullOrEmpty(kv.Value.Value))
                {
                    result.Add(new ParamChange { Name = kv.Value.Key, Old = "", New = kv.Value.Value });
                }
            }
            foreach (var kv in Values)
            {
                if (kv.Key == EditedByKey || kv.Key == BoxKey) continue;
                if (!after.Values.ContainsKey(kv.Key) && !string.IsNullOrEmpty(kv.Value.Value))
                    result.Add(new ParamChange { Name = kv.Value.Key, Old = kv.Value.Value, New = "" });
            }
            return result;
        }

        private static string Trim(string s, int max = MaxValueLength)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0)
            {
                var sb = new StringBuilder(s.Length);
                foreach (var ch in s) sb.Append(ch == '\n' || ch == '\r' ? ' ' : ch);
                s = sb.ToString();
            }
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
