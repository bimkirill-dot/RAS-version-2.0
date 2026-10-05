using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>Подрезка 3D-вида изменений.</summary>
    public enum ChangesCrop
    {
        /// <summary>Рамка вокруг показанных изменений с запасом.</summary>
        Changes,
        /// <summary>Этажи: с уровня по уровень, по всей ширине модели.</summary>
        Levels,
        None
    }

    /// <summary>Уровень модели — для выбора этажей в окне (без Revit API).</summary>
    public sealed class LevelInfo
    {
        public long Id;
        public string Name;
        /// <summary>Отметка, футы (внутренние единицы Revit) — для порядка в списке.</summary>
        public double Elevation;
    }

    /// <summary>
    /// Как раскрашивать 3D-вид: цвета, остальная модель, тени. Выбирается в окне «Показать в 3D…» и запоминается
    /// в ui.json одним объектом (UiState.ChangesStyle).
    /// </summary>
    public sealed class ChangesViewStyle
    {
        public uint Created { get; set; } = ChangesPalette.Created;
        public uint Modified { get; set; } = ChangesPalette.Modified;
        public uint NoNet { get; set; } = ChangesPalette.NoNet;
        /// <summary>Прозрачность остальной модели, %; ChangesPalette.HideBackground — скрыть её.</summary>
        public int Rest { get; set; } = ChangesPalette.Background;
        /// <summary>С тенями («Тонирование с кромками»); иначе «Согласованные цвета» — цвет ровный и ярче.</summary>
        public bool Shaded { get; set; }

        public ChangesViewStyle Clone() => (ChangesViewStyle)MemberwiseClone();
    }

    /// <summary>Что показать на 3D-виде изменений (из окна «Показать в 3D…»).</summary>
    public sealed class ChangesViewRequest
    {
        /// <summary>Начало периода; null — всё время.</summary>
        public DateTimeOffset? From;
        /// <summary>Конец периода (не включая); null — до сейчас.</summary>
        public DateTimeOffset? To;
        /// <summary>Начало — последняя синхронизация текущего пользователя (From находится по журналу).</summary>
        public bool SinceMyLastSync;
        /// <summary>«сегодня», «7 дней», «с 01.10.2026» — для легенды.</summary>
        public string PeriodText;
        /// <summary>«Иванов, Петров» — для легенды; null — все авторы.</summary>
        public string AuthorsText;
        /// <summary>Чьи правки показать; null — всех.</summary>
        public List<string> Authors;
        public bool ShowCreated = true;
        public bool ShowModified = true;
        public bool ShowNoNetChange;
        public bool ColorByAuthor;
        public ChangesViewStyle Style = new ChangesViewStyle();
        public ChangesCrop Crop = ChangesCrop.Changes;
        public long? LevelFrom;
        public long? LevelTo;
        public bool HideFloors;
        public bool HideRoofs;
        public bool HideCeilings;
    }

    /// <summary>
    /// Цвета 3D-вида изменений. Яркие насыщенные оттенки: пастельные и даже средние на белом фоне Revit
    /// теряются за полупрозрачной моделью. Контур — тот же цвет заметно темнее и толстой линией.
    /// </summary>
    public static class ChangesPalette
    {
        public const uint Created = 0x00C853;
        public const uint Modified = 0xFF6D00;
        public const uint NoNet = 0x2962FF;
        /// <summary>Прозрачность остальной модели по умолчанию, %.</summary>
        public const int Background = 65;
        /// <summary>
        /// Прозрачность изменённого элемента, внутри которого другое изменение (новая арматура в балке), %:
        /// иначе его заливка закрывает то, что внутри. Обводка остаётся толстой — элемент по-прежнему виден.
        /// </summary>
        public const int SeeThrough = 55;

        /// <summary>
        /// Остальная модель: серая заливка и кромки. Чем она прозрачнее, тем светлее, но кромки остаются заметными:
        /// без теней форму передают только они, иначе модель превращается в бледные пятна.
        /// </summary>
        public static void RestLook(int transparency, out byte fill, out byte edge)
        {
            if (transparency <= 45) { fill = 200; edge = 120; }
            else if (transparency <= 70) { fill = 210; edge = 150; }
            else { fill = 225; edge = 185; }
        }
        /// <summary>«Скрыть остальную модель — показать только изменения».</summary>
        public const int HideBackground = -1;
        /// <summary>Вес линии контура изменённых элементов (1–16): толстая обводка видна и издалека.</summary>
        public const int EdgeWeight = 6;

        /// <summary>Цвета на выбор в окне (подпись, 0xRRGGBB).</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, uint>> Colors = new[]
        {
            new KeyValuePair<string, uint>("Зелёный", 0x00C853),
            new KeyValuePair<string, uint>("Салатовый", 0x64DD17),
            new KeyValuePair<string, uint>("Бирюзовый", 0x00BFA5),
            new KeyValuePair<string, uint>("Голубой", 0x00B0FF),
            new KeyValuePair<string, uint>("Синий", 0x2962FF),
            new KeyValuePair<string, uint>("Фиолетовый", 0xAA00FF),
            new KeyValuePair<string, uint>("Пурпурный", 0xD500F9),
            new KeyValuePair<string, uint>("Розовый", 0xF50057),
            new KeyValuePair<string, uint>("Красный", 0xD50000),
            new KeyValuePair<string, uint>("Оранжевый", 0xFF6D00),
            new KeyValuePair<string, uint>("Жёлтый", 0xFFD600),
            new KeyValuePair<string, uint>("Коричневый", 0x795548)
        };

        /// <summary>Цвета авторов — по очереди, начиная с самого активного.</summary>
        public static readonly uint[] Authors =
        {
            0x2962FF, 0x00C853, 0xFF6D00, 0xAA00FF, 0xD50000, 0x00BFA5, 0xFFD600, 0x795548, 0xF50057, 0x64DD17, 0x00B0FF, 0x616161
        };

        public static byte R(uint rgb) => (byte)(rgb >> 16);
        public static byte G(uint rgb) => (byte)(rgb >> 8);
        public static byte B(uint rgb) => (byte)rgb;

        /// <summary>Контур: тот же цвет, заметно темнее.</summary>
        public static uint Darker(uint rgb, double k = 0.5) =>
            ((uint)(R(rgb) * k) << 16) | ((uint)(G(rgb) * k) << 8) | (uint)(B(rgb) * k);
    }

    /// <summary>Строка легенды: цвет на виде и сколько им показано.</summary>
    public sealed class LegendEntry
    {
        public string Label;
        public int Count;
        public byte FillR, FillG, FillB;
        public byte LineR, LineG, LineB;
    }

    public sealed class ChangesViewResult
    {
        public bool Shown;
        /// <summary>«7 дней · Иванов» — заголовок легенды.</summary>
        public string Title;
        /// <summary>Почему вид не построен (Shown = false).</summary>
        public string Message;
        public List<LegendEntry> Legend { get; } = new List<LegendEntry>();
        public int Colored;
        /// <summary>Удалены за период — на виде их нет.</summary>
        public int Deleted;
        /// <summary>Изменены по журналу, но в модели их сейчас нет (удалены позже периода, другой вариант).</summary>
        public int NotInModel;
        /// <summary>Не элементы модели: виды, типы, аннотации — в 3D не показываются.</summary>
        public int NotDrawable;
        /// <summary>Без отличий в параметрах — не показаны (флажок выключен).</summary>
        public int NoNetHidden;
        /// <summary>Изменённые, у которых «было» было неизвестно — показаны как изменённые.</summary>
        public int Uncertain;
        /// <summary>Элементы сводных строк массовых операций — по отдельности неизвестны.</summary>
        public int Summary;
        /// <summary>Показано не всё: больше MaxElements.</summary>
        public int Capped;
        /// <summary>Полупрозрачные: внутри них другие изменения.</summary>
        public int SeeThrough;
    }

    /// <summary>
    /// 3D-вид изменений: свой вид пользователя, изменения за период раскрашены, остальная модель бледная.
    /// Рабочие виды не трогаются. Свои транзакции плагин в журнал не пишет (TransactionPrefix).
    /// Вид удаляется, когда закрыли его вкладку (ChangeTracker), а оставшийся — при следующем открытии модели.
    /// Только из контекста Revit API.
    /// </summary>
    public static class ChangesView3D
    {
        /// <summary>Начало имён транзакций плагина: ChangeTracker такие транзакции в журнал не пишет.</summary>
        public const string TransactionPrefix = "RAS-Model History:";
        private const string NamePrefix = "RAS-Model History — изменения";
        /// <summary>Прежнее название плагина (SpyPlugin): оставшиеся с тех пор виды тоже узнаются и удаляются.</summary>
        private const string LegacyTransactionPrefix = "SpyPlugin:";
        private const string LegacyNamePrefix = "SpyPlugin — изменения";
        /// <summary>Больше — тяжело и для Revit, и для глаза.</summary>
        public const int MaxElements = 20000;
        private const double Metre = 1 / 0.3048;

        private struct Style
        {
            public Color Fill;
            public Color Line;
        }

        /// <summary>Заливка — выбранный цвет, контур — тот же темнее.</summary>
        private static Style FromRgb(uint rgb)
        {
            var line = ChangesPalette.Darker(rgb);
            return new Style
            {
                Fill = new Color(ChangesPalette.R(rgb), ChangesPalette.G(rgb), ChangesPalette.B(rgb)),
                Line = new Color(ChangesPalette.R(line), ChangesPalette.G(line), ChangesPalette.B(line))
            };
        }

        /// <summary>Вид этого пользователя: «RAS-Model History — изменения (Полетаев)».</summary>
        public static string ViewName(string user)
        {
            var clean = new string((user ?? "").Select(ch => "\\:{}[]|;<>?`~".IndexOf(ch) >= 0 ? '_' : ch).ToArray()).Trim();
            return NamePrefix + " (" + (clean.Length == 0 ? "пользователь" : clean) + ")";
        }

        public static bool IsOwnTransaction(ICollection<string> names) =>
            names != null && names.Count > 0 && names.All(n => n != null &&
                (n.StartsWith(TransactionPrefix, StringComparison.Ordinal) || n.StartsWith(LegacyTransactionPrefix, StringComparison.Ordinal)));

        public static bool IsChangesViewName(string name) =>
            name != null && (name.StartsWith(NamePrefix + " (", StringComparison.Ordinal) ||
                             name.StartsWith(LegacyNamePrefix + " (", StringComparison.Ordinal));

        /// <summary>
        /// 3D-вид изменений (чей угодно). Если его синхронизировали, он приходит коллегам как «чужое» изменение —
        /// разбор синхронизации его пропускает: это не правка модели.
        /// </summary>
        public static bool IsChangesView(Element el) => el is View3D v && !v.IsTemplate && IsChangesViewName(v.Name);

        /// <summary>Уровни модели по высоте — для подрезки по этажам.</summary>
        public static List<LevelInfo> Levels(Document doc)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Select(l => new LevelInfo { Id = l.Id.Value, Name = l.Name, Elevation = l.ProjectElevation })
                .OrderBy(l => l.Elevation).ToList();
        }

        /// <summary>Построить (или обновить) вид и открыть его.</summary>
        public static ChangesViewResult Show(UIDocument uidoc, DocumentTracker t, ChangesViewRequest req, PeriodDiffResult diff, string user)
        {
            var doc = uidoc.Document;
            var look = req.Style ?? new ChangesViewStyle();
            var result = new ChangesViewResult
            {
                Summary = diff.SummaryElements,
                Title = req.PeriodText + (string.IsNullOrEmpty(req.AuthorsText) ? "" : " · " + req.AuthorsText)
            };

            // что красим
            var targets = new List<KeyValuePair<Element, ElementNet>>();
            var seen = new HashSet<ElementId>();
            foreach (var e in diff.Elements.Values)
            {
                if (e.Change == NetChange.Deleted) { result.Deleted++; continue; }
                if (e.Change == NetChange.Created && !req.ShowCreated) continue;
                if (e.Change == NetChange.Modified && !req.ShowModified) continue;
                if (e.Change == NetChange.NoNetChange && !req.ShowNoNetChange) { result.NoNetHidden++; continue; }
                var el = Find(doc, e);
                if (el == null) { result.NotInModel++; continue; }
                if (!IsDrawable(el)) { result.NotDrawable++; continue; }
                if (!seen.Add(el.Id)) continue;
                targets.Add(new KeyValuePair<Element, ElementNet>(el, e));
            }
            if (targets.Count == 0)
            {
                result.Message = NothingToShow(result);
                return result;
            }
            if (targets.Count > MaxElements)
            {
                result.Capped = targets.Count - MaxElements;
                targets = targets.OrderByDescending(x => x.Value.Last).Take(MaxElements).ToList();
            }

            // цвет и подпись в легенде для каждого элемента; подписи — в порядке легенды
            var paint = new Dictionary<ElementId, KeyValuePair<string, Style>>();
            var order = new List<KeyValuePair<string, Style>>();
            if (req.ColorByAuthor)
            {
                var byAuthor = targets.GroupBy(x => string.IsNullOrEmpty(x.Value.Author) ? "(неизвестно)" : x.Value.Author)
                    .OrderByDescending(g => g.Count()).ToList();
                for (int i = 0; i < byAuthor.Count; i++)
                {
                    var p = new KeyValuePair<string, Style>(byAuthor[i].Key, FromRgb(ChangesPalette.Authors[i % ChangesPalette.Authors.Length]));
                    order.Add(p);
                    foreach (var x in byAuthor[i]) paint[x.Key.Id] = p;
                }
            }
            else
            {
                foreach (var change in new[] { NetChange.Created, NetChange.Modified, NetChange.NoNetChange })
                    order.Add(new KeyValuePair<string, Style>(LabelOf(change), StyleOf(change, look)));
                foreach (var x in targets) paint[x.Key.Id] = order[(int)x.Value.Change];
            }

            var solid = SolidFill(doc);
            var seeThrough = Containers(targets.Select(x => x.Key).ToList());
            View3D view;
            bool reused;
            var colored = new List<ElementId>(targets.Count);
            List<ElementId> hidden;
            using (var tx = new Transaction(doc, TransactionPrefix + " 3D-вид изменений"))
            {
                tx.Start();
                view = PrepareView(doc, uidoc, t, user, out reused);
                // без теней цвет ложится ровно, как выбран; с тенями половина граней темнее и выглядит тусклой
                try { view.DisplayStyle = look.Shaded ? DisplayStyle.ShadingWithEdges : DisplayStyle.FlatColors; }
                catch { /* стиль по умолчанию */ }
                var hideRest = look.Rest == ChangesPalette.HideBackground;
                var background = Background(solid, hideRest ? ChangesPalette.Background : look.Rest);
                foreach (Category c in doc.Settings.Categories)
                {
                    if (c == null || c.CategoryType != CategoryType.Model) continue;
                    try { if (view.IsCategoryOverridable(c.Id)) view.SetCategoryOverrides(c.Id, background); }
                    catch { /* категорию не переопределить — остаётся как есть */ }
                }

                foreach (var x in targets)
                {
                    try
                    {
                        view.SetElementOverrides(x.Key.Id, Highlight(paint[x.Key.Id].Value, solid,
                            seeThrough.Contains(x.Key.Id) ? ChangesPalette.SeeThrough : 0));
                        colored.Add(x.Key.Id);
                    }
                    catch { result.NotDrawable++; }
                }

                var box = req.Crop == ChangesCrop.Changes ? AroundElements(targets.Select(x => x.Key))
                    : req.Crop == ChangesCrop.Levels ? ByLevels(doc, req, targets.Select(x => x.Key))
                    : null;
                if (box != null) view.SetSectionBox(box);
                else view.IsSectionBoxActive = false;

                // «скрыть перекрытия / крыши / потолки» — кроме изменённых: их-то и смотрят
                hidden = HideUnchanged(doc, view, req, colored);
                // «скрыть остальную модель»: временная изоляция — только на этом виде и только пока он открыт
                if (hideRest && colored.Count > 0) view.IsolateElementsTemporary(colored);

                if (tx.Commit() != TransactionStatus.Committed)
                {
                    result.Message = "Revit не дал создать 3D-вид изменений.";
                    return result;
                }
            }
            // состояние — только после фиксации: если транзакция откатилась, прежний вид вернулся как был
            t.ChangesViewId = view.Id.Value;
            t.ChangesViewColored = colored.Select(x => x.Value).ToList();
            t.ChangesViewHidden = hidden.Select(x => x.Value).ToList();
            t.ChangesViewSeenOpen = reused;
            // имя вида — чтобы его удаление вручную узнавалось и не попадало в журнал (Jobs.AddLive)
            try { t.Info[view.Id.Value] = ElementDescriber.Describe(doc, view, t); }
            catch { /* не страшно: удаление после подготовки модели и так не пишется */ }

            // легенда — по тому, что действительно покрашено
            result.Colored = colored.Count;
            var done = new HashSet<ElementId>(colored);
            foreach (var p in order)
            {
                var n = colored.Count(id => paint[id].Key == p.Key);
                if (n > 0) result.Legend.Add(Entry(p.Key, n, p.Value));
            }
            result.Uncertain = targets.Count(x => x.Value.Uncertain && done.Contains(x.Key.Id));
            result.SeeThrough = seeThrough.Count(done.Contains);

            Open(uidoc, view);
            result.Shown = true;
            return result;
        }

        /// <summary>Элемент в модели: по постоянному UniqueId, а если его нет в записях — по ID.</summary>
        private static Element Find(Document doc, ElementNet e)
        {
            try
            {
                // UniqueId известен, а элемента с ним нет — элемент удалён; по ID тогда нашёлся бы чужой
                if (!string.IsNullOrEmpty(e.UniqueId)) return doc.GetElement(e.UniqueId);
                return doc.GetElement(new ElementId(e.Id));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Почему показать нечего — чтобы было понятно, что включить.</summary>
        private static string NothingToShow(ChangesViewResult r)
        {
            var parts = new List<string>();
            if (r.NoNetHidden > 0) parts.Add($"без отличий в параметрах — {r.NoNetHidden} (включите «Изменённые без отличий в параметрах»)");
            if (r.Deleted > 0) parts.Add($"удалено — {r.Deleted} (удалённое на виде не показать)");
            if (r.NotInModel > 0) parts.Add($"уже нет в модели — {r.NotInModel}");
            if (r.NotDrawable > 0) parts.Add($"виды, типы, аннотации — {r.NotDrawable} (в 3D не показываются)");
            if (r.Summary > 0) parts.Add($"в сводных строках массовых операций — {r.Summary}");
            return parts.Count == 0
                ? "За выбранный период изменений нет."
                : "Показать на 3D-виде нечего: " + string.Join("; ", parts) + ".";
        }

        /// <summary>Скрыть неизменённые перекрытия, крыши, потолки (изменённые остаются). Возвращает скрытые.</summary>
        private static List<ElementId> HideUnchanged(Document doc, View3D view, ChangesViewRequest req, ICollection<ElementId> colored)
        {
            var hidden = new List<ElementId>();
            var cats = new List<BuiltInCategory>();
            if (req.HideFloors) cats.Add(BuiltInCategory.OST_Floors);
            if (req.HideRoofs) cats.Add(BuiltInCategory.OST_Roofs);
            if (req.HideCeilings) cats.Add(BuiltInCategory.OST_Ceilings);
            if (cats.Count == 0) return hidden;
            var keep = new HashSet<ElementId>(colored);
            foreach (var el in new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
            {
                if (!keep.Contains(el.Id) && el.CanBeHidden(view)) hidden.Add(el.Id);
            }
            if (hidden.Count > 0) view.HideElements(hidden);
            return hidden;
        }

        /// <summary>Найти свой вид и подготовить к новой раскраске (или создать новый). Состояние DocumentTracker не трогает.</summary>
        /// <param name="reused">Вид открыт сейчас и использован заново (иначе создан новый).</param>
        private static View3D PrepareView(Document doc, UIDocument uidoc, DocumentTracker t, string user, out bool reused)
        {
            reused = false;
            var name = ViewName(user);
            View3D existing = null;
            if (t.ChangesViewId.HasValue) existing = doc.GetElement(new ElementId(t.ChangesViewId.Value)) as View3D;
            existing ??= new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (existing != null)
            {
                if (uidoc.ActiveView == null || uidoc.ActiveView.Id != existing.Id)
                {
                    // не активен — проще создать заново (открытую вкладку Revit закроет сам)
                    doc.Delete(existing.Id);
                }
                else
                {
                    // открыт сейчас — удалить нельзя: снимаем прежнюю раскраску, скрытие и изоляцию и красим заново
                    try
                    {
                        if (existing.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
                            existing.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    }
                    catch { /* изоляции не было */ }
                    var tracked = t.ChangesViewId == existing.Id.Value;
                    if (tracked && t.ChangesViewHidden.Count > 0)
                    {
                        try { existing.UnhideElements(t.ChangesViewHidden.Select(x => new ElementId(x)).ToList()); }
                        catch { /* часть элементов уже удалена */ }
                    }
                    var ids = tracked && t.ChangesViewColored.Count > 0
                        ? t.ChangesViewColored.Select(x => new ElementId(x)).ToList()
                        : new FilteredElementCollector(doc, existing.Id).WhereElementIsNotElementType().ToElementIds().ToList();
                    var clean = new OverrideGraphicSettings();
                    foreach (var id in ids)
                    {
                        try { existing.SetElementOverrides(id, clean); } catch { /* элемента уже нет */ }
                    }
                    reused = true;
                    return existing;
                }
            }

            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(x => x.ViewFamily == ViewFamily.ThreeDimensional);
            var view = View3D.CreateIsometric(doc, type.Id);
            for (int i = 1; ; i++)
            {
                try
                {
                    view.Name = i == 1 ? name : name + " " + i;
                    break;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) when (i < 20)
                {
                    // имя занято (например, видом другого типа) — следующий номер
                }
            }
            // шаблон из типа вида управлял бы видимостью — тогда раскраска не сработает
            if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
            try { view.DetailLevel = ViewDetailLevel.Medium; } catch { /* уровень детализации по умолчанию */ }
            return view;
        }

        /// <summary>Открыть вид и показать всё, что на нём.</summary>
        private static void Open(UIDocument uidoc, View3D view)
        {
            try
            {
                if (uidoc.ActiveView == null || uidoc.ActiveView.Id != view.Id) uidoc.ActiveView = view;
                var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                ui?.ZoomToFit();
            }
            catch (Exception ex)
            {
                Diag.Log("Changes view open failed", ex);
                try { uidoc.RequestViewChange(view); } catch { /* откроют из диспетчера проекта */ }
            }
        }

        /// <summary>Удалить вид (вкладку уже закрыли). Активный вид Revit удалить не даст — тогда сначала другой вид.</summary>
        /// <returns>null — удалён, иначе — почему нет.</returns>
        public static string Delete(UIDocument uidoc, DocumentTracker t)
        {
            if (!t.ChangesViewId.HasValue) return null;
            var doc = uidoc.Document;
            var id = new ElementId(t.ChangesViewId.Value);
            if (doc.GetElement(id) == null)
            {
                Forget(t); // уже нет (отменили создание, удалили вручную)
                return null;
            }
            if (uidoc.ActiveView != null && uidoc.ActiveView.Id == id)
            {
                var other = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId != id);
                if (other == null) return "Это единственный открытый вид — откройте другой вид и закройте вкладку 3D-вида изменений.";
                uidoc.ActiveView = doc.GetElement(other.ViewId) as View;
            }
            try
            {
                using (var tx = new Transaction(doc, TransactionPrefix + " удаление 3D-вида изменений"))
                {
                    tx.Start();
                    doc.Delete(id);
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                Diag.Log("Changes view delete failed", ex);
                Forget(t); // не пытаемся снова на каждом простое; останется — удалится при следующем открытии модели
                return "Не удалось удалить 3D-вид изменений: " + ex.Message;
            }
            Forget(t);
            return null;
        }

        /// <summary>
        /// Виды этого пользователя, оставшиеся с прошлых сеансов (Revit закрылся, вкладку не закрыли), — удалить.
        /// Открытый сейчас такой вид не удалить — он берётся под наблюдение: закроют вкладку — удалится.
        /// </summary>
        public static int DeleteLeftovers(UIDocument uidoc, DocumentTracker t, string user)
        {
            var doc = uidoc.Document;
            var name = ViewName(user);
            var open = new HashSet<long>(uidoc.GetOpenUIViews().Select(v => v.ViewId.Value));
            var mine = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate && (v.Name == name || v.Name.StartsWith(name + " ", StringComparison.Ordinal)))
                .ToList();
            var openOne = mine.FirstOrDefault(v => open.Contains(v.Id.Value));
            if (openOne != null && !t.ChangesViewId.HasValue)
            {
                t.ChangesViewId = openOne.Id.Value;
                t.ChangesViewSeenOpen = true;
            }
            var ids = mine.Where(v => !open.Contains(v.Id.Value)).Select(v => v.Id).ToList();
            if (ids.Count == 0) return 0;
            using (var tx = new Transaction(doc, TransactionPrefix + " удаление 3D-вида изменений"))
            {
                tx.Start();
                doc.Delete(ids);
                tx.Commit();
            }
            return ids.Count;
        }

        private static void Forget(DocumentTracker t)
        {
            t.ChangesViewId = null;
            t.ChangesViewColored = new List<long>();
            t.ChangesViewHidden = new List<long>();
            t.ChangesViewSeenOpen = false;
        }

        // ───────────────────────────── оформление ─────────────────────────────

        private static bool IsDrawable(Element el) =>
            el.Category != null && el.Category.CategoryType == CategoryType.Model && !(el is ElementType) && !(el is View) && !el.ViewSpecific;

        private static Style StyleOf(NetChange change, ChangesViewStyle look) =>
            FromRgb(change == NetChange.Created ? look.Created : change == NetChange.Modified ? look.Modified : look.NoNet);

        private static string LabelOf(NetChange change) =>
            change == NetChange.Created ? "создано" : change == NetChange.Modified ? "изменено" : "без отличий в параметрах";

        private static LegendEntry Entry(string label, int count, Style s) => new LegendEntry
        {
            Label = label, Count = count,
            FillR = s.Fill.Red, FillG = s.Fill.Green, FillB = s.Fill.Blue,
            LineR = s.Line.Red, LineG = s.Line.Green, LineB = s.Line.Blue
        };

        private static ElementId SolidFill(Document doc)
        {
            var fill = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => { var p = f.GetFillPattern(); return p.IsSolidFill && p.Target == FillPatternTarget.Drafting; });
            return fill?.Id ?? ElementId.InvalidElementId;
        }

        /// <summary>
        /// Остальная модель: серая, прозрачная, кромки тонкие и серые (без полутона — он смешивал всё с белым и
        /// модель становилась слишком бледной). Изменения видны и сквозь неё.
        /// </summary>
        private static OverrideGraphicSettings Background(ElementId solid, int transparency)
        {
            ChangesPalette.RestLook(transparency, out var fillGray, out var edgeGray);
            var o = new OverrideGraphicSettings();
            var fill = new Color(fillGray, fillGray, fillGray);
            var edge = new Color(edgeGray, edgeGray, edgeGray);
            if (solid != ElementId.InvalidElementId)
            {
                o.SetSurfaceForegroundPatternId(solid);
                o.SetSurfaceForegroundPatternColor(fill);
                o.SetCutForegroundPatternId(solid);
                o.SetCutForegroundPatternColor(fill);
            }
            o.SetProjectionLineColor(edge);
            o.SetCutLineColor(edge);
            o.SetProjectionLineWeight(1);
            o.SetCutLineWeight(1);
            o.SetSurfaceTransparency(Math.Max(0, Math.Min(100, transparency)));
            o.SetHalftone(false);
            return o;
        }

        private static OverrideGraphicSettings Highlight(Style s, ElementId solid, int transparency)
        {
            var o = new OverrideGraphicSettings();
            if (solid != ElementId.InvalidElementId)
            {
                o.SetSurfaceForegroundPatternId(solid);
                o.SetSurfaceForegroundPatternColor(s.Fill);
                o.SetCutForegroundPatternId(solid);
                o.SetCutForegroundPatternColor(s.Fill);
            }
            o.SetProjectionLineColor(s.Line);
            o.SetCutLineColor(s.Line);
            o.SetProjectionLineWeight(ChangesPalette.EdgeWeight); // толстая обводка — видна и издалека
            o.SetCutLineWeight(ChangesPalette.EdgeWeight);
            o.SetSurfaceTransparency(transparency);
            o.SetHalftone(false);
            return o;
        }

        /// <summary>
        /// Изменённые элементы, внутри которых другое изменение: центр габаритов меньшего элемента лежит в габаритах
        /// большего (новая арматура в балке, закладная в стене). Их делаем полупрозрачными.
        /// Проверяются не больше MaxInner внутренних (сначала мелкие) — для огромных выборок проверка была бы долгой.
        /// </summary>
        private static HashSet<ElementId> Containers(IList<Element> elements)
        {
            const int MaxInner = 3000;
            var boxes = new List<Box>(elements.Count);
            foreach (var e in elements)
            {
                BoundingBoxXYZ b = null;
                try { b = e.get_BoundingBox(null); } catch { /* без габаритов — пропускаем */ }
                if (b == null) continue;
                var min = b.Transform.OfPoint(b.Min);
                var max = b.Transform.OfPoint(b.Max);
                var box = new Box
                {
                    Id = e.Id,
                    MinX = Math.Min(min.X, max.X), MinY = Math.Min(min.Y, max.Y), MinZ = Math.Min(min.Z, max.Z),
                    MaxX = Math.Max(min.X, max.X), MaxY = Math.Max(min.Y, max.Y), MaxZ = Math.Max(min.Z, max.Z)
                };
                box.Volume = Math.Max(1e-9, (box.MaxX - box.MinX) * (box.MaxY - box.MinY) * (box.MaxZ - box.MinZ));
                boxes.Add(box);
            }
            var result = new HashSet<ElementId>();
            if (boxes.Count < 2) return result;
            // крупные — по возрастанию левой границы: для точки достаточно просмотреть тех, кто начинается левее неё
            var outer = boxes.OrderBy(b => b.MinX).ToList();
            var starts = outer.Select(b => b.MinX).ToArray();
            foreach (var inner in boxes.OrderBy(b => b.Volume).Take(MaxInner))
            {
                double cx = (inner.MinX + inner.MaxX) / 2, cy = (inner.MinY + inner.MaxY) / 2, cz = (inner.MinZ + inner.MaxZ) / 2;
                var last = Array.BinarySearch(starts, cx);
                if (last < 0) last = ~last - 1;
                else while (last + 1 < starts.Length && starts[last + 1] <= cx) last++;
                for (int i = last; i >= 0; i--)
                {
                    var o = outer[i];
                    // в 1,5 раза больше по объёму: два одинаковых соседних элемента не делают друг друга прозрачными
                    if (ReferenceEquals(o, inner) || o.Volume < inner.Volume * 1.5) continue;
                    if (cx > o.MaxX || cy < o.MinY || cy > o.MaxY || cz < o.MinZ || cz > o.MaxZ) continue;
                    result.Add(o.Id);
                }
            }
            return result;
        }

        private sealed class Box
        {
            public ElementId Id;
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ, Volume;
        }

        // ───────────────────────────── подрезка ─────────────────────────────

        /// <summary>Рамка вокруг элементов с запасом: 1 м плюс 5 % размера.</summary>
        private static BoundingBoxXYZ AroundElements(IEnumerable<Element> elements)
        {
            var b = new Bounds();
            foreach (var el in elements) b.Add(el.get_BoundingBox(null));
            return b.IsEmpty ? null : b.ToBox(Metre);
        }

        /// <summary>Этажи: по высоте — от уровня «с» (с запасом вниз на перекрытие) до уровня над уровнем «по»; по плану — вся модель.</summary>
        private static BoundingBoxXYZ ByLevels(Document doc, ChangesViewRequest req, IEnumerable<Element> changed)
        {
            if (!req.LevelFrom.HasValue || !req.LevelTo.HasValue) return AroundElements(changed);
            var levels = Levels(doc);
            var a = levels.FirstOrDefault(l => l.Id == req.LevelFrom.Value);
            var b = levels.FirstOrDefault(l => l.Id == req.LevelTo.Value);
            if (a == null || b == null) return AroundElements(changed);
            if (a.Elevation > b.Elevation) { var swap = a; a = b; b = swap; }
            var above = levels.FirstOrDefault(l => l.Elevation > b.Elevation + 0.01);
            var zMin = a.Elevation - 0.5 * Metre;
            var zMax = above != null ? above.Elevation - 0.05 * Metre : b.Elevation + 4 * Metre;

            // по плану — габарит стен и перекрытий: этого хватает, а перебирать все колонны, балки и фундаменты
            // на большой модели — заметная пауза
            var plan = new Bounds();
            var cats = new List<BuiltInCategory> { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors };
            foreach (var el in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
                plan.Add(el.get_BoundingBox(null));
            if (plan.IsEmpty) foreach (var el in changed) plan.Add(el.get_BoundingBox(null));
            if (plan.IsEmpty) return null;
            plan.MinZ = zMin;
            plan.MaxZ = zMax;
            return plan.ToBox(Metre, padZ: false);
        }

        /// <summary>Габарит набора рамок (в координатах модели).</summary>
        private sealed class Bounds
        {
            public double MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue;
            public double MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue;
            public bool IsEmpty => MinX > MaxX;

            public void Add(BoundingBoxXYZ box)
            {
                if (box == null) return;
                var tr = box.Transform ?? Transform.Identity;
                foreach (var p in new[] { tr.OfPoint(box.Min), tr.OfPoint(box.Max) })
                {
                    MinX = Math.Min(MinX, p.X); MinY = Math.Min(MinY, p.Y); MinZ = Math.Min(MinZ, p.Z);
                    MaxX = Math.Max(MaxX, p.X); MaxY = Math.Max(MaxY, p.Y); MaxZ = Math.Max(MaxZ, p.Z);
                }
            }

            public BoundingBoxXYZ ToBox(double margin, bool padZ = true)
            {
                double Pad(double min, double max) => margin + 0.05 * (max - min);
                var px = Pad(MinX, MaxX);
                var py = Pad(MinY, MaxY);
                var pz = padZ ? Pad(MinZ, MaxZ) : 0;
                return new BoundingBoxXYZ
                {
                    Min = new XYZ(MinX - px, MinY - py, MinZ - pz),
                    Max = new XYZ(MaxX + px, MaxY + py, MaxZ + pz)
                };
            }
        }
    }
}
