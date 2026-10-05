using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.ModelHistory.Tracking;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// Действия с моделью из немодальной панели. Панель работает вне контекста Revit API,
    /// поэтому действия выполняются через ExternalEvent.
    /// </summary>
    public sealed class RevitActions : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Action<UIApplication>> _queue = new ConcurrentQueue<Action<UIApplication>>();
        private readonly ExternalEvent _event;
        private readonly ChangeTracker _tracker;

        /// <summary>Создавать только в контексте Revit API (OnStartup).</summary>
        public RevitActions(ChangeTracker tracker)
        {
            _tracker = tracker;
            _event = ExternalEvent.Create(this);
        }

        public string GetName() => "RAS-Model History: действия с моделью";

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(app); }
                catch (Exception ex) { Diag.Log("Revit action failed", ex); }
            }
        }

        public void Run(Action<UIApplication> action)
        {
            _queue.Enqueue(action);
            _event.Raise();
        }

        /// <summary>Показать элемент: открыть вид / выделить и приблизить.</summary>
        public void ShowElement(string modelKey, long id, Action<string> report)
        {
            Run(app =>
            {
                var uidoc = ActiveDoc(app, modelKey, report);
                if (uidoc == null) return;
                var doc = uidoc.Document;
                var el = doc.GetElement(new ElementId(id));
                if (el == null)
                {
                    report("Элемента " + id + " в модели нет (удалён).");
                    return;
                }
                if (el is View view)
                {
                    if (view.IsTemplate)
                    {
                        report("«" + view.Name + "» — шаблон вида. Откройте: Вид → Шаблоны видов → Управление шаблонами видов.");
                        return;
                    }
                    try
                    {
                        uidoc.RequestViewChange(view);
                        report("Открыт вид «" + view.Name + "».");
                    }
                    catch (Exception ex)
                    {
                        report("Не удалось открыть вид: " + ex.Message);
                    }
                    return;
                }
                if (el is ElementType || el is Family)
                {
                    report("«" + ElementDescriber.GetName(el) + "» — тип/семейство, на виде его показать нельзя. Найдите его в Диспетчере проекта → Семейства.");
                    return;
                }
                try
                {
                    uidoc.Selection.SetElementIds(new List<ElementId> { el.Id });
                    uidoc.ShowElements(el.Id);
                    report("Элемент " + id + " выделен и показан.");
                }
                catch (Exception)
                {
                    report("Элемент " + id + " выделен, но показать его на видах не удалось (скрыт, в другом варианте или не отображается).");
                }
            });
        }

        /// <summary>Выделить в модели набор элементов (например, все из отфильтрованного журнала).</summary>
        public void SelectElements(string modelKey, IReadOnlyCollection<long> ids, Action<string> report)
        {
            Run(app =>
            {
                var uidoc = ActiveDoc(app, modelKey, report);
                if (uidoc == null) return;
                var doc = uidoc.Document;
                var existing = new List<ElementId>();
                int missing = 0, notSelectable = 0;
                foreach (var v in ids)
                {
                    var el = doc.GetElement(new ElementId(v));
                    if (el == null) { missing++; continue; }
                    if (el is ElementType || el is View || el is Family || el.Category == null) { notSelectable++; continue; }
                    existing.Add(el.Id);
                }
                try
                {
                    uidoc.Selection.SetElementIds(existing);
                    var msg = "Выделено элементов: " + existing.Count + ".";
                    if (missing > 0) msg += " Удалены: " + missing + ".";
                    if (notSelectable > 0) msg += " Не выделяются (виды, типы, настройки): " + notSelectable + ".";
                    report(msg);
                }
                catch (Exception ex)
                {
                    report("Не удалось выделить: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// 3D-вид изменений: окно настроек — в контексте Revit (нужны уровни модели), журнал за период — в фоне
        /// (Revit не подвисает), сам вид — снова в контексте Revit.
        /// </summary>
        /// <param name="ask">Показать окно «Показать в 3D…»; null — отмена.</param>
        /// <param name="compute">Прочитать журнал за период и посчитать итог (фоновый поток, без Revit API).</param>
        public void ShowChangesView(string modelKey, Func<IReadOnlyList<LevelInfo>, ChangesViewRequest> ask,
            Func<ChangesViewRequest, PeriodDiffResult> compute, Action<string> report, Action<ChangesViewResult> done)
        {
            Run(app =>
            {
                var uidoc = ActiveDoc(app, modelKey, report);
                if (uidoc == null) return;
                ChangesViewRequest request;
                try
                {
                    request = ask(ChangesView3D.Levels(uidoc.Document));
                }
                catch (Exception ex)
                {
                    // иначе человек нажал «3D», и ничего не произошло: ошибка была бы только в журнале ошибок
                    Diag.Log("Changes view dialog failed", ex);
                    report("Не удалось открыть окно «3D»: " + ex.Message);
                    return;
                }
                if (request == null) return;
                report("3D-вид изменений: читаю журнал за период…");
                System.Threading.Tasks.Task.Run(() =>
                {
                    PeriodDiffResult diff;
                    try
                    {
                        diff = compute(request);
                    }
                    catch (Exception ex)
                    {
                        Diag.Log("Changes view diff failed", ex);
                        report("Не удалось прочитать журнал: " + ex.Message);
                        return;
                    }
                    Run(app2 =>
                    {
                        var ui = ActiveDoc(app2, modelKey, report);
                        if (ui == null) return;
                        var t = _tracker.Find(ui.Document);
                        try
                        {
                            done(ChangesView3D.Show(ui, t, request, diff, _tracker.CurrentUser));
                        }
                        catch (Exception ex)
                        {
                            Diag.Log("Changes view failed", ex);
                            report("Не удалось построить 3D-вид изменений: " + ex.Message);
                        }
                    });
                });
            });
        }

        /// <summary>Удалить 3D-вид изменений (кнопка в панели).</summary>
        public void CloseChangesView(string modelKey, Action<string> report)
        {
            Run(app =>
            {
                var uidoc = ActiveDoc(app, modelKey, report);
                if (uidoc == null) return;
                var error = _tracker.CloseChangesView(uidoc);
                report(error ?? "3D-вид изменений удалён.");
            });
        }

        private UIDocument ActiveDoc(UIApplication app, string modelKey, Action<string> report)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                report("Нет активной модели.");
                return null;
            }
            var t = _tracker.Find(uidoc.Document);
            if (t == null || !string.Equals(t.Model.Key, modelKey, StringComparison.OrdinalIgnoreCase))
            {
                report("Журнал относится к другой модели — сначала перейдите в неё.");
                return null;
            }
            return uidoc;
        }
    }
}
