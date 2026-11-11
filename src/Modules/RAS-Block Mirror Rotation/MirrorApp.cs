using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RASv2.BlockMirrorRotation.Logic;
using RASv2.BlockMirrorRotation.Revit;
using RASv2.BlockMirrorRotation.UI;
using RASv2.Core;

namespace RASv2.BlockMirrorRotation
{
    /// <summary>
    /// Кнопка «Повернуть блок» (DECISIONS.md): проверки → окно → запомнить оформление видов на листах → команда Revit
    /// «Отразить проект» (ось — в её окне) → в простое Revit, когда модель изменилась, — найти отражение по точкам элементов
    /// модели → восстановить (одна группа операций — одно Ctrl+Z) → итог и журнал.
    /// </summary>
    public sealed class MirrorApp
    {
        private const string Title = "Повернуть блок";
        // команда Revit ушла, а модель так и не изменилась — через столько ждать перестаём (человек закрыл её окно)
        private static readonly TimeSpan Wait = TimeSpan.FromMinutes(15);

        public static MirrorApp Instance { get; private set; }

        private IModuleContext _context;
        private Pending _pending;
        private ExternalEvent _show;
        private ShowElement _showHandler;

        /// <summary>Запомненное до команды Revit: ждём, когда модель изменится.</summary>
        private sealed class Pending
        {
            public Document Doc;
            public Snapshot Snapshot;
            public bool RenameGrids;
            public DateTime Posted;
            public bool Changed;
            public string Journal;
        }

        public void Start(IModuleContext context)
        {
            Instance = this;
            _context = context;
            var button = context.AddButton("Run", "Повернуть\nблок", typeof(MirrorCommand),
                "Отразить блок командой Revit «Отразить проект» и вернуть оформление на видах: оси, марки, отметки, размеры, текст",
                icon: "Mirror");
            button.LongDescription = "Откройте файл блока (в нём никто не работает) и нажмите. Плагин запомнит оформление на всех видах " +
                                     "на листах, Revit отразит модель (ось выберите в его окне), а плагин вернёт оформление на место: " +
                                     "концы и кружки осей, имена осей, марки, высотные отметки, размеры, текст. Что не вышло — списком.";
            context.Events.DocumentChanged(OnChanged);
            context.Events.Idling(OnIdling);
        }

        public void Run(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null || doc.IsFamilyDocument)
                {
                    TaskDialog.Show(Title, "Откройте файл блока (проект) и нажмите «Повернуть блок» в нём.");
                    return;
                }
                if (doc.IsReadOnly)
                {
                    TaskDialog.Show(Title, "Файл открыт только для чтения — отразить его нельзя.");
                    return;
                }
                if (_pending != null && _pending.Doc.IsValidObject && DateTime.Now - _pending.Posted < Wait && !_pending.Changed)
                {
                    TaskDialog.Show(Title, "Плагин ещё ждёт команду Revit «Отразить проект», запущенную раньше. Выполните или отмените её окно.");
                    return;
                }
                var busy = BusyWorksets(doc, app.Application.Username);
                if (busy != null)
                {
                    TaskDialog.Show(Title, "В файле работают другие: " + busy + ".\nОтражать можно, только когда в файле никто не работает.");
                    return;
                }
                var views = Snapshot.ViewsOnSheets(doc);
                var window = new MirrorWindow(views.Count);
                new WindowInteropHelper(window).Owner = app.MainWindowHandle;
                if (window.ShowDialog() != true) return;

                if (_show == null)
                {
                    // внешнее событие создаётся только в команде Revit — здесь
                    _showHandler = new ShowElement(_context.Log);
                    _show = ExternalEvent.Create(_showHandler);
                }

                Snapshot snapshot;
                var progress = new ProgressWindow("Запоминаю оформление", views.Count);
                new WindowInteropHelper(progress).Owner = app.MainWindowHandle;
                progress.Show();
                try
                {
                    snapshot = Snapshot.Capture(doc, views, i => progress.Step(i, views[i].Name));
                }
                finally
                {
                    progress.Close();
                }
                var journal = new StringBuilder();
                journal.AppendLine($"Повернуть блок — {doc.Title}, {DateTime.Now:dd.MM.yyyy HH:mm}, {Environment.UserName}, Revit {_context.RevitYear}, сборка {_context.BuildId}");
                journal.AppendLine($"Запомнено: видов {snapshot.Views.Count}, осей и уровней {snapshot.Views.Sum(v => v.Datums.Count)}, марок {snapshot.Views.Sum(v => v.Tags.Count)}, " +
                                   $"отметок {snapshot.Views.Sum(v => v.Spots.Count)}, размеров {snapshot.Views.Sum(v => v.Dimensions.Count)}, текстов {snapshot.Views.Sum(v => v.Texts.Count)}, " +
                                   $"прочего {snapshot.Views.Sum(v => v.Others.Count)}; точек модели для поиска отражения — {snapshot.ModelPoints.Count}");
                _pending = new Pending { Doc = doc, Snapshot = snapshot, RenameGrids = window.RenameGrids, Posted = DateTime.Now, Journal = journal.ToString() };
                _context.Log.Info("Повернуть блок: оформление запомнено (" + doc.Title + "), запускаю «Отразить проект»");
                app.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.MirrorProject));
            }
            catch (Exception ex)
            {
                _pending = null;
                _context.Log.Error("Повернуть блок: не запущено", ex);
                TaskDialog.Show(Title, "Не получилось запустить — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»).");
            }
        }

        /// <summary>Рабочие наборы, занятые другими (совместная работа); никто не занял — null.</summary>
        private static string BusyWorksets(Document doc, string me)
        {
            if (!doc.IsWorkshared) return null;
            var busy = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .Where(w => !string.IsNullOrEmpty(w.Owner) && !string.Equals(w.Owner, me, StringComparison.OrdinalIgnoreCase))
                .Select(w => "«" + w.Name + "» — " + w.Owner).ToList();
            return busy.Count == 0 ? null : string.Join(", ", busy.Take(5)) + (busy.Count > 5 ? " и ещё " + (busy.Count - 5) : "");
        }

        private void OnChanged(object sender, DocumentChangedEventArgs e)
        {
            var p = _pending;
            if (p != null && !p.Changed && e.GetDocument().Equals(p.Doc)) p.Changed = true;
        }

        private void OnIdling(object sender, IdlingEventArgs e)
        {
            var p = _pending;
            if (p == null) return;
            if (!p.Doc.IsValidObject)
            {
                _pending = null;
                return;
            }
            if (!p.Changed)
            {
                if (DateTime.Now - p.Posted > Wait)
                {
                    _pending = null;
                    _context.Log.Info("Повернуть блок: «Отразить проект» так и не изменила модель — перестал ждать");
                }
                return;
            }
            _pending = null;
            Restore((UIApplication)sender, p);
        }

        private void Restore(UIApplication app, Pending p)
        {
            var doc = p.Doc;
            var journal = new StringBuilder(p.Journal);
            try
            {
                var now = Snapshot.ModelPointsOf(doc, p.Snapshot.ModelPoints.Keys.ToList());
                var pairs = now.Select(kv => new PointPair(p.Snapshot.ModelPoints[kv.Key].X, p.Snapshot.ModelPoints[kv.Key].Y, kv.Value.X, kv.Value.Y)).ToList();
                var map = PlanTransform.Fit(pairs);
                // 1 мм в футах
                const double mm = 0.001 / 0.3048;
                if (map == null)
                {
                    Fail(app, journal, "Не удалось понять, как Revit отразил модель (мало элементов модели с точкой) — оформление не восстановлено.");
                    return;
                }
                journal.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "Преобразование по {0} точкам: x' = {1:0.######}x + {2:0.######}y + {3:0.###}; y' = {4:0.######}x + {5:0.######}y + {6:0.###}; расхождение {7:0.####} фут",
                    pairs.Count, map.A, map.B, map.C, map.D, map.E, map.F, map.Residual));
                if (map.IsIdentity(mm) && map.Residual < mm)
                {
                    journal.AppendLine("Модель не отражена — оформление не трогаю.");
                    Save(journal);
                    TaskDialog.Show(Title, "Отражение отменено — модель и оформление не менялись.");
                    return;
                }
                if (!map.IsMirror(1e-4) || map.Residual > mm)
                {
                    Fail(app, journal, "Модель изменилась, но не как отражение целиком (часть элементов сдвинулась иначе) — оформление не восстановлено. " +
                                       "Если это ошибка, отмените (Ctrl+Z) и пришлите разработчику журнал.");
                    return;
                }

                Report report;
                var views = p.Snapshot.Views;
                var progress = new ProgressWindow("Восстанавливаю оформление", views.Count);
                new WindowInteropHelper(progress).Owner = app.MainWindowHandle;
                progress.Show();
                try
                {
                    using (var group = new TransactionGroup(doc, "RAS: оформление после отражения"))
                    {
                        group.Start();
                        report = new Restorer(doc, map).Run(p.Snapshot, p.RenameGrids, i => progress.Step(i, views[i].Name));
                        group.Assimilate();
                    }
                }
                finally
                {
                    progress.Close();
                }
                journal.AppendLine($"Восстановлено: видов {report.Views} (не вышло {report.ViewsFailed}), осей и уровней {report.Datums}, марок {report.Tags}, " +
                                   $"отметок {report.Spots}, размеров целы {report.Dimensions}, построено заново {report.DimensionsRebuilt}, текстов {report.Texts}, " +
                                   $"прочее на месте {report.Others}, осей переименовано {report.Renamed}; строк итога {report.Rows.Count + report.RowsDropped}");
                foreach (var n in report.Notes) journal.AppendLine("  · " + n);
                foreach (var r in report.Rows) journal.AppendLine($"  {r.View} | {r.Element} | {r.Problem}");
                if (report.RowsDropped > 0) journal.AppendLine($"  … и ещё {report.RowsDropped} строк — не показаны");
                var path = Save(journal);
                _context.Log.Info("Повернуть блок: оформление восстановлено (" + doc.Title + "), журнал — " + path);
                var window = new ResultWindow(report, path, (view, element) =>
                {
                    _showHandler.Set(doc, view, element);
                    _show.Raise();
                });
                new WindowInteropHelper(window).Owner = app.MainWindowHandle;
                window.Show();
            }
            catch (Exception ex)
            {
                _context.Log.Error("Повернуть блок: оформление не восстановлено", ex);
                journal.AppendLine("Ошибка: " + ex);
                Save(journal);
                TaskDialog.Show(Title, "Восстановление оформления прервалось — подробности в журнале плагина («О плагине» → «Показать журнал ошибок»). " +
                                       "Отражение осталось; отменить всё — Ctrl+Z.");
            }
        }

        private void Fail(UIApplication app, StringBuilder journal, string message)
        {
            journal.AppendLine(message);
            var path = Save(journal);
            TaskDialog.Show(Title, message + (path != null ? "\n\nЖурнал: " + path : ""));
        }

        /// <summary>Журнал запуска — %LocalAppData%\RAS_v_2.0\RAS-Block Mirror Rotation\Журналы; не записался — null.</summary>
        private string Save(StringBuilder journal)
        {
            try
            {
                var dir = Path.Combine(_context.LocalDataDir, "Журналы");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "Повернуть блок " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".log");
                File.WriteAllText(path, journal.ToString(), Encoding.UTF8);
                return path;
            }
            catch (Exception ex)
            {
                _context.Log.Error("Повернуть блок: журнал запуска не записан", ex);
                return null;
            }
        }
    }

    /// <summary>Щелчок по строке итога: открыть вид, выделить и показать элемент (внешнее событие — окно итога немодальное).</summary>
    internal sealed class ShowElement : IExternalEventHandler
    {
        private readonly ModuleLog _log;
        private Document _doc;
        private ElementId _view, _element;

        public ShowElement(ModuleLog log) => _log = log;

        public void Set(Document doc, ElementId view, ElementId element)
        {
            _doc = doc;
            _view = view;
            _element = element;
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null || _doc == null || !_doc.IsValidObject || !uidoc.Document.Equals(_doc)) return;
                if (_view != null && _doc.GetElement(_view) is View view) uidoc.ActiveView = view;
                if (_element == null || _doc.GetElement(_element) == null) return;
                uidoc.Selection.SetElementIds(new List<ElementId> { _element });
                uidoc.ShowElements(_element);
            }
            catch (Exception ex)
            {
                _log.Error("Повернуть блок: элемент из итога не показан", ex);
            }
        }

        public string GetName() => "RAS: показать элемент из итога";
    }
}
