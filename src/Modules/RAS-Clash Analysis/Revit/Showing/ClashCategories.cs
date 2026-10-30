using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.Showing
{
    /// <summary>
    /// «Категории элементов» в виде коллизии (разработчик, 29.10.2026; DECISIONS.md, «Категории элементов»): что скрыто в
    /// исходном файле (открытая модель) и в связанном (связи). Связь показывается «по основному виду» — видимость категорий
    /// нашего вида действует и на неё, а скрыть категорию только в связи Revit API не умеет. Поэтому: скрыта в обоих — категория
    /// скрыта в виде; только в исходном — элементы модели этой категории скрыты в виде по одному; только в связанном — скрытый
    /// фильтр вида «категория, тип — не из открытой модели». Элементы коллизии видны всегда: их категория целиком не скрывается,
    /// скрываются остальные элементы в ней. Подкатегории — сразу везде. Только в контексте Revit API.
    /// </summary>
    internal static class ClashCategories
    {
        private static readonly ElementId Links = new ElementId(BuiltInCategory.OST_RvtLinks);

        /// <summary>Ключ категории: имя BuiltInCategory, у своих категорий модели (без него) — название.</summary>
        public static string Key(Category c) => c.BuiltInCategory != BuiltInCategory.INVALID ? c.BuiltInCategory.ToString() : c.Name;

        private static bool IsModel(Category c) => c.CategoryType == CategoryType.Model && !c.Id.Equals(Links);

        /// <summary>Категории модели и их подкатегории для окна — по алфавиту.</summary>
        public static List<CategoryItem> List(Document doc)
        {
            var culture = CultureInfo.GetCultureInfo("ru-RU");
            var items = new List<CategoryItem>();
            foreach (var c in doc.Settings.Categories.Cast<Category>().Where(IsModel).OrderBy(c => c.Name, StringComparer.Create(culture, true)))
            {
                var key = Key(c);
                items.Add(new CategoryItem { Key = key, Name = c.Name });
                foreach (var sub in c.SubCategories.Cast<Category>().OrderBy(s => s.Name, StringComparer.Create(culture, true)))
                    items.Add(new CategoryItem { Key = CategoryChoice.SubKey(key, sub.Name), Name = sub.Name, Parent = key });
            }
            return items;
        }

        /// <summary>
        /// Видимость категорий в виде (внутри транзакции, до временной изоляции): «Категории элементов» — без «Изолировать»;
        /// при нём категории не действуют (разработчик, 29.10.2026: кнопка доступна только без «Изолировать»), скрыты все
        /// категории, кроме категорий элементов коллизии (разработчик, 30.10.2026: «при „Изолировать“ — только элементы
        /// модели, точка»; обобщённые модели больше не оставляются).
        /// </summary>
        public static void Apply(Document doc, View view, ClashViews views, List<ClashShow.Found> found, ViewSettings settings)
        {
            if (settings.Isolate)
                settings = settings.Copy().With(s => { s.HiddenHost.Clear(); s.HiddenLink.Clear(); });
            var clash = new HashSet<ElementId>(found.Select(f => (f.Element.Category?.Parent ?? f.Element.Category)?.Id).Where(id => id != null));
            var hostOnly = new List<Category>();
            var linkOnly = new List<Category>();
            foreach (Category c in doc.Settings.Categories)
            {
                if (!IsModel(c) || !c.get_AllowsVisibilityControl(view)) continue;
                var key = Key(c);
                var host = settings.HiddenHost.Contains(key);
                var link = settings.HiddenLink.Contains(key);
                var whole = !clash.Contains(c.Id) && (settings.Isolate || (host && link));
                if (view.GetCategoryHidden(c.Id) != whole) view.SetCategoryHidden(c.Id, whole);
                if (!whole)
                {
                    if (host) hostOnly.Add(c);
                    if (link) linkOnly.Add(c);
                }
                foreach (Category sub in c.SubCategories)
                {
                    if (!sub.get_AllowsVisibilityControl(view)) continue;
                    var hide = CategoryChoice.SubHidden(settings, CategoryChoice.SubKey(key, sub.Name));
                    if (view.GetCategoryHidden(sub.Id) != hide) view.SetCategoryHidden(sub.Id, hide);
                }
            }
            HideHost(doc, view, views, found, hostOnly);
            HideLinked(doc, view, views, found, linkOnly);
        }

        /// <summary>Только в исходном файле: элементы модели этих категорий скрыты в виде по одному, кроме элементов коллизии; скрытые прошлый раз — снова видны.</summary>
        private static void HideHost(Document doc, View view, ClashViews views, List<ClashShow.Found> found, List<Category> categories)
        {
            var before = views.Hidden(doc, view.Id).Where(id => doc.GetElement(id)?.IsHidden(view) == true).ToList();
            if (before.Count > 0) view.UnhideElements(before);
            var skip = new HashSet<ElementId>(found.Where(f => f.Link == null).Select(f => f.Element.Id));
            var ids = new List<ElementId>();
            foreach (var c in categories)
                ids.AddRange(new FilteredElementCollector(doc).OfCategoryId(c.Id).WhereElementIsNotElementType()
                    .Where(e => !skip.Contains(e.Id) && !e.IsHidden(view) && e.CanBeHidden(view)).Select(e => e.Id));
            if (ids.Count > 0) view.HideElements(ids);
            views.SetHidden(doc, view.Id, ids);
        }

        /// <summary>
        /// Только в связанном файле: скрытый фильтр вида на категорию — «тип не из открытой модели», по имени семейства и имени
        /// типа (номер типа Revit в фильтре не позволяет — журнал 29.10.2026). Своих элементов фильтр не задевает; у связи остаются
        /// видны элементы тех же семейства и типа, что есть в открытой модели (например, те же семейства отверстий), и тип
        /// элемента коллизии. Revit не дал фильтр — категория в связи видна, причина — в журнале.
        /// </summary>
        private static void HideLinked(Document doc, View view, ClashViews views, List<ClashShow.Found> found, List<Category> categories)
        {
            if (categories.Count == 0) return;
            var familyName = new ElementId(BuiltInParameter.ALL_MODEL_FAMILY_NAME);
            var typeName = new ElementId(BuiltInParameter.ALL_MODEL_TYPE_NAME);
            var hostTypes = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                .Where(t => t.Category != null).ToLookup(t => t.Category.Id, t => (t.FamilyName, t.Name));
            foreach (var c in categories)
            {
                try
                {
                    var ids = new List<ElementId> { c.Id };
                    var filterable = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, ids);
                    if (!filterable.Contains(typeName))
                    {
                        views.Log.Info($"Категории элементов: «{c.Name}» в связи не скрыта — Revit не позволяет фильтр по имени типа");
                        continue;
                    }
                    var byFamily = filterable.Contains(familyName);
                    var types = hostTypes[c.Id]
                        .Concat(found.Where(f => f.Link != null && c.Id.Equals((f.Element.Category?.Parent ?? f.Element.Category)?.Id))
                            .Select(f => f.Element.Document.GetElement(f.Element.GetTypeId()) as ElementType).Where(t => t != null)
                            .Select(t => (t.FamilyName, t.Name)))
                        .Where(t => !string.IsNullOrEmpty(t.Name)).Distinct().ToList();
                    var parts = types.Select(t => byFamily && !string.IsNullOrEmpty(t.FamilyName)
                        ? (ElementFilter)new LogicalOrFilter(Not(familyName, t.FamilyName), Not(typeName, t.Name))
                        : Not(typeName, t.Name)).ToList();
                    var rule = parts.Count == 0 ? null : parts.Count == 1 ? parts[0] : new LogicalAndFilter(parts);
                    var filter = views.NewFilter(doc, "связь — " + c.Name, ids, rule);
                    view.AddFilter(filter.Id);
                    view.SetFilterVisibility(filter.Id, false);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException || ex is InvalidOperationException)
                {
                    views.Log.Error($"Категории элементов: «{c.Name}» в связи не скрыта", ex);
                }
            }
        }

        private static ElementParameterFilter Not(ElementId parameter, string value) =>
            new ElementParameterFilter(ParameterFilterRuleFactory.CreateNotEqualsRule(parameter, value));
    }
}
