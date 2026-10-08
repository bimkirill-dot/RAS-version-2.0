using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>Формирует понятное человеку описание элемента Revit.</summary>
    public static class ElementDescriber
    {
        /// <summary>Классы без категории, которые всё равно важны пользователю.</summary>
        private static readonly HashSet<string> SignificantClassNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Family", "Material", "FilterElement", "ParameterFilterElement", "SelectionFilterElement",
            "ParameterElement", "SharedParameterElement", "GlobalParameter", "Phase", "PhaseFilter",
            "DesignOption", "FillPatternElement", "LinePatternElement", "ViewSheetSet", "BrowserOrganization",
            "AreaScheme", "ProjectLocation", "SiteLocation", "Revision", "RevisionSettings", "ProjectInfo",
            "PrintSetting", "ExportDWGSettings", "ExportDGNSettings", "ColorFillScheme", "AppearanceAssetElement",
            "TemporaryViewModes", "WorksharingDisplaySettings", "PropertySetElement", "StairsPath",
            "ScheduleSheetInstance", "RevisionCloud", "AssemblyInstance", "Group", "RevitLinkInstance",
            "ImportInstance", "PointCloudInstance", "Viewport", "SunAndShadowSettings"
        };

        /// <summary>Категории, которые считаются «шумом» (внутренние построения Revit).</summary>
        private static readonly HashSet<string> NoiseCategoryNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "OST_WeakDims", "OST_Constraints", "OST_SketchLines", "OST_IOSSketchGrid"
        };

        private static readonly Dictionary<string, string> ViewTypeNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FloorPlan"] = "План этажа",
            ["CeilingPlan"] = "План потолка",
            ["EngineeringPlan"] = "План несущих конструкций",
            ["AreaPlan"] = "План зон",
            ["Elevation"] = "Фасад",
            ["Section"] = "Разрез",
            ["Detail"] = "Узел (выноска)",
            ["ThreeD"] = "3D-вид",
            ["Schedule"] = "Спецификация",
            ["ColumnSchedule"] = "Спецификация колонн",
            ["PanelSchedule"] = "Спецификация щита",
            ["DrawingSheet"] = "Лист",
            ["DraftingView"] = "Чертёжный вид",
            ["Legend"] = "Легенда",
            ["Report"] = "Отчёт",
            ["CostReport"] = "Отчёт",
            ["LoadsReport"] = "Отчёт о нагрузках",
            ["PresureLossReport"] = "Отчёт о потерях давления",
            ["SystemsAnalysisReport"] = "Отчёт анализа систем",
            ["Walkthrough"] = "Обход",
            ["Rendering"] = "Визуализация",
            ["ProjectBrowser"] = "Диспетчер проекта",
            ["SystemBrowser"] = "Диспетчер систем",
            ["Internal"] = "Внутренний вид",
            ["Undefined"] = "Вид"
        };

        /// <param name="t">Трекер документа — для пула строк (экономия памяти).</param>
        /// <param name="compact">Для массовой подготовки: без UniqueId.</param>
        public static ElementInfo Describe(Document doc, Element el, DocumentTracker t = null, bool compact = false)
        {
            var info = new ElementInfo
            {
                Id = el.Id.Value,
                ClassName = string.Intern(el.GetType().Name)
            };
            if (!compact)
            {
                try { info.UniqueId = el.UniqueId; } catch { /* ignore */ }
            }

            Category cat = null;
            try { cat = el.Category; } catch { /* ignore */ }
            if (cat == null && el is Family fam)
            {
                try { cat = fam.FamilyCategory; } catch { /* ignore */ }
            }
            info.Category = cat != null ? string.Intern(SafeName(cat)) : "";
            info.Group = Classify(el, cat);
            info.Name = t != null ? t.Pool(GetName(el)) : GetName(el);
            info.TypeName = Intern(GetTypeName(doc, el));
            info.IsNoise = IsNoise(el, cat);
            info.Parents = ParentsOf(el);

            try
            {
                var levelId = el.LevelId;
                if (IsValid(levelId)) info.Level = Intern(doc.GetElement(levelId)?.Name);
            }
            catch { /* ignore */ }

            try
            {
                var ownerViewId = el.OwnerViewId;
                if (IsValid(ownerViewId)) info.OwnerView = Intern(GetName(doc.GetElement(ownerViewId)));
            }
            catch { /* ignore */ }

            if (doc.IsWorkshared)
            {
                try
                {
                    var ws = doc.GetWorksetTable().GetWorkset(el.WorksetId);
                    if (ws != null) info.Workset = Intern(ws.Name);
                }
                catch { /* ignore */ }
            }
            return info;
        }

        /// <summary>
        /// От чего элемент зависит и с чем Revit его удалит (DeletionCascade): основа (стена у двери, перекрытие у проёма,
        /// балка у арматуры), вложенность, тип, уровень, вид-владелец (аннотации вида), группа, видовой экран — вид,
        /// марка — отмеченные элементы, размер — элементы, к которым он привязан. Только дешёвые свойства — вызывается
        /// для каждого элемента при подготовке модели.
        /// </summary>
        public static long[] ParentsOf(Element el)
        {
            var list = new List<long>(4);
            void Add(ElementId id)
            {
                if (IsValid(id) && id.Value != el.Id.Value && !list.Contains(id.Value)) list.Add(id.Value);
            }
            try
            {
                switch (el)
                {
                    case FamilyInstance fi:
                        Add(fi.Host?.Id);
                        Add(fi.SuperComponent?.Id);
                        break;
                    case Opening op:
                        Add(op.Host?.Id);
                        break;
                    case Rebar rebar:
                        Add(rebar.GetHostId());
                        break;
                    case RebarInSystem rs:
                        Add(rs.GetHostId());
                        break;
                    case AreaReinforcement ar:
                        Add(ar.GetHostId());
                        break;
                    case PathReinforcement pr:
                        Add(pr.GetHostId());
                        break;
                    case IndependentTag tag:
                        foreach (var id in tag.GetTaggedLocalElementIds()) Add(id);
                        break;
                    case Viewport vp:
                        Add(vp.ViewId);
                        break;
                    case Dimension dim:
                        foreach (Reference r in dim.References) Add(r?.ElementId);
                        break;
                }
            }
            catch { /* у части элементов свойства бросают исключение — родителей по ним просто нет */ }
            try { if (!(el is ElementType)) Add(el.GetTypeId()); } catch { /* ignore */ }
            try { Add(el.LevelId); } catch { /* ignore */ }
            try { Add(el.OwnerViewId); } catch { /* ignore */ }
            try { Add(el.GroupId); } catch { /* ignore */ }
            return list.Count == 0 ? null : list.ToArray();
        }

        /// <summary>Быстрое описание (только категория, класс, имя) — для элементов сверх лимита записей.</summary>
        public static ElementInfo QuickInfo(Element el)
        {
            Category cat = null;
            try { cat = el.Category; } catch { /* ignore */ }
            return new ElementInfo
            {
                Id = el.Id.Value,
                ClassName = string.Intern(el.GetType().Name),
                Category = cat != null ? string.Intern(SafeName(cat)) : "",
                Group = Classify(el, cat),
                Name = GetName(el),
                TypeName = "",
                IsNoise = IsNoise(el, cat)
            };
        }

        /// <summary>Сведения для элемента, о котором ничего не известно (удалён до того, как попал в кэш).</summary>
        public static ElementInfo Unknown(long id) => new ElementInfo
        {
            Id = id,
            Category = "",
            ClassName = "",
            Group = ObjectGroup.Unknown,
            Name = ChangeRecord.UnknownElementName,
            TypeName = ""
        };

        public static ObjectGroup Classify(Element el, Category cat)
        {
            switch (el)
            {
                case View v when v.IsTemplate: return ObjectGroup.ViewTemplate;
                case ViewSheet _: return ObjectGroup.Sheet;
                case ViewSchedule _: return ObjectGroup.Schedule;
                case View _: return ObjectGroup.View;
                case Family _: return ObjectGroup.Family;
                case ElementType _: return ObjectGroup.ElementType;
                case Material _: return ObjectGroup.Material;
                case FilterElement _: return ObjectGroup.Filter;
                case ParameterElement _: return ObjectGroup.Parameter;
            }
            if (cat != null)
            {
                try
                {
                    switch (cat.CategoryType)
                    {
                        case CategoryType.Model: return ObjectGroup.Model;
                        case CategoryType.Annotation: return ObjectGroup.Annotation;
                        case CategoryType.AnalyticalModel: return ObjectGroup.Analytical;
                    }
                }
                catch { /* ignore */ }
            }
            return ObjectGroup.Settings;
        }

        public static bool IsNoise(Element el, Category cat)
        {
            if (el is View || el is ElementType || el is Family) return false;
            if (cat == null) return !SignificantClassNames.Contains(el.GetType().Name);
            try
            {
                var bic = Enum.ToObject(typeof(BuiltInCategory), cat.Id.Value).ToString();
                if (NoiseCategoryNames.Contains(bic)) return true;
            }
            catch { /* ignore */ }
            return false;
        }

        public static string GetName(Element el)
        {
            if (el == null) return "";
            try
            {
                switch (el)
                {
                    case ViewSheet sheet:
                        return $"{sheet.SheetNumber} - {sheet.Name}";
                    case View view:
                        return view.Name;
                }
                var name = el.Name;
                return string.IsNullOrEmpty(name) ? el.GetType().Name : name;
            }
            catch
            {
                return el.GetType().Name;
            }
        }

        public static string GetTypeName(Document doc, Element el)
        {
            try
            {
                if (el is View view)
                {
                    var vt = ViewTypeName(view);
                    if (!view.IsTemplate && IsValid(view.ViewTemplateId))
                    {
                        var template = doc.GetElement(view.ViewTemplateId);
                        if (template != null) vt += " | шаблон: " + template.Name;
                    }
                    return vt;
                }
                if (el is ElementType et) return FormatType(et);

                var typeId = el.GetTypeId();
                if (IsValid(typeId) && doc.GetElement(typeId) is ElementType type) return FormatType(type);
            }
            catch { /* ignore */ }
            return "";
        }

        public static string ViewTypeName(View view)
        {
            try
            {
                var key = view.ViewType.ToString();
                var name = ViewTypeNames.TryGetValue(key, out var ru) ? ru : key;
                return view.IsTemplate ? "Шаблон: " + name : name;
            }
            catch
            {
                return "Вид";
            }
        }

        private static string FormatType(ElementType et)
        {
            string family = "";
            try { family = et.FamilyName; } catch { /* ignore */ }
            var name = SafeName(et);
            return string.IsNullOrEmpty(family) ? name : family + " : " + name;
        }

        private static string SafeName(Element el)
        {
            try { return el.Name ?? ""; } catch { return ""; }
        }

        private static string SafeName(Category cat)
        {
            try { return cat.Name ?? ""; } catch { return ""; }
        }

        public static bool IsValid(ElementId id) => id != null && id.Value != ElementId.InvalidElementId.Value;

        private static string Intern(string s) => string.IsNullOrEmpty(s) ? s : string.Intern(s);

        public static string GroupDisplayName(ObjectGroup g)
        {
            switch (g)
            {
                case ObjectGroup.Model: return "Элемент модели";
                case ObjectGroup.Annotation: return "Аннотация";
                case ObjectGroup.View: return "Вид";
                case ObjectGroup.ViewTemplate: return "Шаблон вида";
                case ObjectGroup.Sheet: return "Лист";
                case ObjectGroup.Schedule: return "Спецификация";
                case ObjectGroup.ElementType: return "Тип";
                case ObjectGroup.Family: return "Семейство";
                case ObjectGroup.Material: return "Материал";
                case ObjectGroup.Filter: return "Фильтр";
                case ObjectGroup.Parameter: return "Параметр";
                case ObjectGroup.Analytical: return "Аналитика";
                case ObjectGroup.Settings: return "Настройки / прочее";
                default: return "Неизвестно";
            }
        }
    }
}
