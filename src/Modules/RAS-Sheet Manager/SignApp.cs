using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RASv2.Core;
using RASv2.SheetManager.Logic;
using RASv2.SheetManager.Signing;
using RASv2.SheetManager.UI;

namespace RASv2.SheetManager
{
    /// <summary>
    /// Кнопка «Подпись» (панель «Менеджер листов»): предупреждение → окно (папка, очистка новых, список) → добавление в
    /// семейство подписей → отчёт. Общие настройки — Data\RAS-Sheet Manager\settings.json на сервере (копия — на компьютере),
    /// личное (последняя папка, масштаб по семействам) — в личной папке модуля.
    /// </summary>
    public sealed class SignApp
    {
        private static readonly TimeSpan ServerWait = TimeSpan.FromSeconds(5);
        public static SignApp Instance { get; private set; }

        private IModuleContext _context;

        private string SharedSettings => Path.Combine(_context.SharedDataDir, "settings.json");
        private string SettingsCopy => Path.Combine(_context.LocalDataDir, "settings-copy.json");
        private string PersonalPath => Path.Combine(_context.UserDataDir, "settings.json");

        public void Start(IModuleContext context)
        {
            Instance = this;
            _context = context;
            var button = context.AddButton("Sign", "Подпись", typeof(SignCommand),
                "Очистить DWG-подписи и добавить их в семейство подписей", icon: "Sign");
            button.LongDescription = "Откройте семейство подписей. Выберите папку с подписями: новые очистятся сами (AutoCAD) — " +
                                     "в семейство пойдут только чистые линии нужного размера. Отметьте, кого добавить, — " +
                                     "таблица выбора, параметр и подпись появятся одной операцией (одно Ctrl+Z).";
        }

        public void Run(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null || !doc.IsFamilyDocument)
            {
                TaskDialog.Show("Подпись", "Откройте семейство подписей и нажмите «Подпись» в нём.");
                return;
            }
            var warn = new TaskDialog("Подпись")
            {
                MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                MainInstruction = "Убедитесь, что вы находитесь в нужном семействе подписей",
                MainContent = "Открыто семейство «" + doc.Title + "».",
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Ok,
            };
            if (warn.Show() != TaskDialogResult.Ok) return;
            var view = doc.ActiveView;
            if (view == null || view.IsTemplate || view is View3D || view.ViewType == ViewType.Schedule || view.ViewType == ViewType.ProjectBrowser ||
                view.ViewType == ViewType.SystemBrowser || view.ViewType == ViewType.Internal)
            {
                TaskDialog.Show("Подпись", "Откройте вид семейства, на котором стоят подписи (обычно «Лист»), и нажмите «Подпись» ещё раз.");
                return;
            }

            var settings = LoadSettings(out var offline);
            var family = new SignFamily(doc, settings, Path.Combine(_context.LocalDataDir, "Таблицы выбора"));
            var session = new SignSession
            {
                Family = family,
                Tables = family.TableChoices(),
                Settings = settings,
                SettingsOffline = offline,
                Personal = SignPersonal.Load(PersonalPath),
                WorkRoot = SignFolder.WorkRoot(Path.Combine(_context.LocalDataDir, "work")),
                PreviewCache = Path.Combine(_context.LocalDataDir, "Картинки подписей"),
                SaveSettings = SaveSettings,
            };
            session.Anchor = family.Anchor(out session.AnchorSpread);

            var window = new SignWindow(session);
            new WindowInteropHelper(window).Owner = app.MainWindowHandle;
            if (window.ShowDialog() != true || session.Jobs.Count == 0) return;

            // точка: как у остальных подписей; подписей нет — начало координат или указать на виде
            var point = session.Anchor ?? XYZ.Zero;
            if (session.Anchor == null && session.PickPoint)
            {
                try { point = uidoc.Selection.PickPoint("Укажите центр подписи"); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return; }
            }

            SignPlan.Number(session.Jobs.Select(j => j.Item), session.State);
            var report = family.Apply(view, session.State, session.SourceEncoding, session.TableExisted, session.Jobs, point,
                session.Scale, session.RemoveUnused, session.Backup);
            _context.Log.Info("Подпись: «" + doc.Title + "», таблица «" + report.Table + "», добавлено " + session.Jobs.Count(j => j.Ok) +
                               " из " + session.Jobs.Count + (report.Error != null ? "; отменено: " + report.Error : ""));

            session.Personal.LastFolder = session.Folder ?? "";
            session.Personal.Scales[doc.Title] = session.Scale;
            try { session.Personal.Save(PersonalPath); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { _context.Log.Error("Личные настройки «Подписи» не сохранены", ex); }

            ShowReport(session, report);
        }

        private static void ShowReport(SignSession s, SignReport r)
        {
            var ok = s.Jobs.Count(j => j.Ok);
            var dialog = new TaskDialog("Подпись");
            if (r.Error != null)
            {
                dialog.MainIcon = TaskDialogIcon.TaskDialogIconWarning;
                dialog.MainInstruction = "Ничего не добавлено";
                dialog.MainContent = r.Error;
                dialog.Show();
                return;
            }
            dialog.MainInstruction = "Добавлено подписей: " + ok + " из " + s.Jobs.Count;
            var text = new StringBuilder();
            text.AppendLine("Таблица выбора: «" + r.Table + "», кодировка " + EncodingName(r.Encoding) + ".");
            text.AppendLine("Проверка: " + r.Check + ".");
            if (r.Encoding == TableEncodings.Cp1251 && s.Jobs.Any(j => j.Ok && SignNames.HasKazakh(j.Item.Name)))
                text.AppendLine("Казахские буквы в таблице заменены русскими: в «" + s.State.Key + "» фамилию вписывают русскими буквами (Әбілов → Абилов).");
            text.AppendLine("Центр подписи: " + r.Point + "; масштаб " + r.Scale + ".");
            if (r.Painted > 0) text.AppendLine("Перекрашено в цвет подписей: " + r.Painted + ".");
            if (r.RemovedTables.Count > 0) text.AppendLine("Удалены таблицы: " + string.Join(", ", r.RemovedTables) + ".");
            if (r.Backup != null) text.AppendLine("Таблица до изменений: " + r.Backup);
            dialog.MainContent = text.ToString().TrimEnd();
            dialog.ExpandedContent = string.Join(Environment.NewLine, s.Jobs.Select(j => j.Item.Name + (j.Item.Number != null ? " (№" + j.Item.Number + ")" : "") + " — " + j.Result));
            if (ok < s.Jobs.Count) dialog.MainIcon = TaskDialogIcon.TaskDialogIconWarning;
            dialog.Show();
        }

        private static string EncodingName(string e) =>
            e == TableEncodings.Utf8Bom ? "UTF-8 с BOM" : e == TableEncodings.Utf8 ? "UTF-8" : e == TableEncodings.Cp1251 ? "Windows-1251" : e ?? "—";

        // ───────────────────────── общие настройки: сервер, копия на компьютере ─────────────────────────

        /// <summary>С сервера (не дольше 5 с); не ответил — копия на компьютере (offline = true); нет и её — по умолчанию.</summary>
        private SignSettings LoadSettings(out bool offline)
        {
            offline = false;
            string text = null;
            var task = Task.Run(() => File.Exists(SharedSettings) ? File.ReadAllText(SharedSettings) : "");
            try
            {
                if (task.Wait(ServerWait)) text = task.Result;
            }
            catch (AggregateException ex) { _context.Log.Error("Настройки «Подписи» на сервере не прочитаны", ex.InnerException ?? ex); }
            if (text == null)
            {
                offline = true;
                text = File.Exists(SettingsCopy) ? SafeRead(SettingsCopy) : null;
            }
            else if (text.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(_context.LocalDataDir);
                    File.WriteAllText(SettingsCopy, text);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* без копии — в следующий раз с сервера */ }
            }
            return (string.IsNullOrEmpty(text) ? null : SignSettings.FromJson(text)) ?? new SignSettings();
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        /// <summary>Записать общие настройки на сервер (через временный файл) и копию на компьютер. null — сохранено.</summary>
        private string SaveSettings(SignSettings s)
        {
            s.ChangedBy = Environment.UserName;
            s.ChangedAt = DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            var json = s.ToJson();
            var task = Task.Run(() =>
            {
                Directory.CreateDirectory(_context.SharedDataDir);
                var temp = SharedSettings + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(SharedSettings)) File.Replace(temp, SharedSettings, null);
                else File.Move(temp, SharedSettings);
            });
            try
            {
                if (!task.Wait(ServerWait)) return "сервер не ответил за " + ServerWait.TotalSeconds + " с";
            }
            catch (AggregateException ex)
            {
                _context.Log.Error("Настройки «Подписи» не записаны на сервер", ex.InnerException ?? ex);
                return (ex.InnerException ?? ex).Message;
            }
            try
            {
                Directory.CreateDirectory(_context.LocalDataDir);
                File.WriteAllText(SettingsCopy, json);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* копия не обязательна */ }
            return null;
        }
    }
}
