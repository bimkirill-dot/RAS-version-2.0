using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;
using RASv2.ClashAnalysis.Navisworks.Logic;
using NwApp = Autodesk.Navisworks.Api.Application;
using NwColor = Autodesk.Navisworks.Api.Color;

namespace RASv2.ClashAnalysis.Navisworks
{
    /// <summary>
    /// Вход сборки — его вызывает загрузчик для Navisworks (src\Navisworks\Loader) по имени: Execute(команда, сборка, папка, канал).
    /// Сигнатуру менять только вместе с загрузчиком.
    /// </summary>
    public static class Entry
    {
        public static int Execute(string command, string buildId, string buildDir, string channel)
        {
            Log.Write("Команда «" + command + "», сборка " + buildId + ", канал «" + channel + "»");
            if (command == "ClashAnalysis.FitViews") return FitViews.Run(channel == "dev");
            MessageBox.Show("Неизвестная команда: " + command, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 0;
        }

        public const string Title = "RAS v. 2.0 — подстроить виды";
    }

    /// <summary>
    /// «Подстроить виды» (DECISIONS.md модуля, «Navisworks: подстроить виды коллизий»): во всех проверках Clash Detective файла
    /// каждой коллизии — вид, где оба элемента видны и не закрывают друг друга (Logic\ViewPick: перебор направлений, оценка по
    /// маленьким картинкам). Вид записывается в саму коллизию — через COM (InwOclTestResult.ViewPoint): в .NET API у коллизии
    /// вид только читается (прежняя попытка, ClashViewFixer v6, 09.2026, на этом остановилась). Прототип: проверить, что вид
    /// записывается и показывается Clash Detective, и сколько занимает.
    /// </summary>
    internal static class FitViews
    {
        private const int ImageWidth = 160, ImageHeight = 120;
        /// <summary>Сборка разработчика — картинки, по которым выбран вид, сохраняются для разбора (первые коллизии).</summary>
        private const int DebugClashes = 5;

        public static int Run(bool debug)
        {
            var doc = NwApp.ActiveDocument;
            if (doc == null || doc.IsClear)
            {
                MessageBox.Show("Откройте файл Navisworks с проверками Clash Detective.", Entry.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            var clash = doc.GetClash();
            var results = new List<(ClashTest Test, ClashResult Result)>();
            foreach (var test in clash.TestsData.Tests.OfType<ClashTest>()) Collect(test, test.Children, results);
            if (results.Count == 0)
            {
                MessageBox.Show("В файле нет коллизий в проверках Clash Detective — сначала запустите проверки.", Entry.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            var tests = results.Select(r => r.Test.DisplayName).Distinct().Count();
            // уже подобранные (и подправленные вручную) — с сохранённым видом: при повторном запуске их не трогать (разработчик, 23.10.2026)
            var fresh = results.Where(r => !r.Result.HasSavedViewpoint).ToList();
            var answer = MessageBox.Show(
                $"Коллизий: {results.Count} в {tests} проверках, из них без сохранённого вида — {fresh.Count}.{Environment.NewLine}{Environment.NewLine}" +
                "«Да» — подобрать вид только им (уже подобранные и подправленные вручную не трогать).\n" +
                "«Нет» — подобрать заново всем.\n«Отмена» — ничего не делать.\n\n" +
                "Сохраните файл до этого — так можно вернуть прежние виды.",
                Entry.Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return 0;
            if (answer == DialogResult.Yes) results = fresh;
            if (results.Count == 0)
            {
                MessageBox.Show("У всех коллизий вид уже есть — подбирать нечего.", Entry.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            var debugDir = debug ? Path.Combine(RasPaths.Local, "Navisworks", "Разбор", DateTime.Now.ToString("yyyy-MM-dd HHmmss")) : null;

            var com = ComResults(out var comProblem);
            if (com == null) Log.Write("Виды в коллизии не записываются: " + comProblem);

            var original = doc.CurrentViewpoint.CreateCopy();
            doc.CurrentSelection.Clear();
            var candidates = ViewPick.Candidates();
            int fitted = 0, written = 0, skipped = 0;
            var watch = Stopwatch.StartNew();
            var progress = NwApp.BeginProgress(Entry.Title, "Подбираю виды коллизий…");
            try
            {
                for (var i = 0; i < results.Count; i++)
                {
                    if (!progress.Update(i / (double)results.Count)) break; // «Отмена»
                    var (test, result) = results[i];
                    try
                    {
                        InwOclTestResult target = null;
                        if (com != null && !com.TryGetValue(Key(test.DisplayName, result.DisplayName), out target))
                            Log.Write($"Коллизия не найдена через COM: «{test.DisplayName}» / «{result.DisplayName}»");
                        var name = $"«{test.DisplayName}» / «{result.DisplayName}»";
                        var dump = debugDir != null && i < DebugClashes ? Path.Combine(debugDir, $"{i + 1:00} {Safe(result.DisplayName)}") : null;
                        if (!FitOne(doc, name, result, candidates, target, dump, out var saved)) { skipped++; continue; }
                        fitted++;
                        if (saved) written++;
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        Log.Write($"Коллизия «{test.DisplayName}» / «{result.DisplayName}» пропущена", ex);
                    }
                }
            }
            finally
            {
                NwApp.EndProgress();
                doc.Models.ResetAllTemporaryMaterials();
                doc.CurrentViewpoint.CopyFrom(original);
            }
            watch.Stop();
            var perClash = fitted > 0 ? watch.Elapsed.TotalSeconds / fitted : 0;
            Log.Write($"Подстроено {fitted} из {results.Count}, записано в коллизии {written}, пропущено {skipped}; " +
                      $"{watch.Elapsed.TotalSeconds:0.0} с, {perClash:0.00} с на коллизию, направлений {candidates.Count}, картинка {ImageWidth}×{ImageHeight}");
            var text = new StringBuilder();
            text.AppendLine($"Подстроено видов: {fitted} из {results.Count}.");
            if (com == null) text.AppendLine("Записать виды в коллизии не удалось: " + comProblem);
            else text.AppendLine($"Записано в коллизии: {written}.");
            if (skipped > 0) text.AppendLine($"Пропущено: {skipped} (подробности — в журнале).");
            if (debugDir != null) Log.Write("Картинки для разбора: " + debugDir);
            text.AppendLine($"Время: {watch.Elapsed.TotalSeconds:0} с ({perClash:0.0} с на коллизию).");
            MessageBox.Show(text.ToString(), Entry.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        private static void Collect(ClashTest test, SavedItemCollection items, List<(ClashTest, ClashResult)> output)
        {
            foreach (var item in items)
            {
                if (item is ClashResultGroup group) Collect(test, group.Children, output);
                else if (item is ClashResult result) output.Add((test, result));
            }
        }

        private static string Key(string test, string result) => (test ?? "") + "\u001f" + (result ?? "");

        private static string Safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string((name ?? "").Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
        }

        /// <summary>Коллизии через COM — для записи вида: «проверка / коллизия» → результат. Нет Clash Detective в COM — null.</summary>
        private static Dictionary<string, InwOclTestResult> ComResults(out string problem)
        {
            problem = null;
            try
            {
                var state = ComApiBridge.State;
                InwOpClashElement element = null;
                foreach (var plugin in state.Plugins()) if (plugin is InwOpClashElement e) { element = e; break; }
                if (element == null) { problem = "нет Clash Detective в COM"; return null; }
                var map = new Dictionary<string, InwOclTestResult>();
                foreach (InwOclClashTest test in element.Tests())
                    foreach (InwOclTestResult result in test.results())
                        map[Key(test.name, result.name)] = result;
                return map;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                Log.Write("COM Clash Detective недоступен", ex);
                return null;
            }
        }

        /// <summary>
        /// Подобрать вид одной коллизии и записать его в коллизию (target, через COM). Вид записывается, пока остальное ещё скрыто
        /// и цвета уже сняты: если Navisworks сохраняет в виде и видимость, в коллизии будет «скрыть остальные», а не вся модель
        /// (разработчик, 23.10.2026: «на подобранных видах видно другие объекты модели»). Нет элементов — false.
        /// </summary>
        private static bool FitOne(Document doc, string name, ClashResult result, List<ViewCandidate> candidates, InwOclTestResult target,
            string dump, out bool saved)
        {
            saved = false;
            var first = result.Item1;
            var second = result.Item2;
            if (first == null || second == null)
            {
                Log.Write($"{name}: пропущена — нет {(first == null ? "первого" : "второго")} элемента (элемент коллизии не найден в модели)");
                return false;
            }
            if (dump != null) Directory.CreateDirectory(dump);
            var zone = ViewPick.Zone(BoxOf(first.BoundingBox()), BoxOf(second.BoundingBox()));
            var zoom = new BoundingBox3D(new Point3D(zone.MinX, zone.MinY, zone.MinZ), new Point3D(zone.MaxX, zone.MaxY, zone.MaxZ));
            var hidden = HideAllBut(doc, first, second);
            try
            {
                doc.Models.OverrideTemporaryColor(new[] { first }, NwColor.Red);
                doc.Models.OverrideTemporaryColor(new[] { second }, NwColor.Green);
                Viewpoint best = null;
                var bestScore = double.NegativeInfinity;
                var bestIndex = -1;
                var scores = new StringBuilder();
                for (var k = 0; k < candidates.Count; k++)
                {
                    var c = candidates[k];
                    var vp = doc.CurrentViewpoint.CreateCopy();
                    vp.AlignDirection(new Vector3D(c.X, c.Y, c.Z));
                    vp.AlignUp(c.Elevation > 80 ? new Vector3D(0, 1, 0) : new Vector3D(0, 0, 1));
                    vp.ZoomBox(zoom);
                    doc.CurrentViewpoint.CopyFrom(vp);
                    int red, green, total;
                    using (var image = doc.ActiveView.GenerateImage(ImageGenerationStyle.Scene, ImageWidth, ImageHeight))
                    {
                        Count(image, out red, out green, out total);
                        if (dump != null) image.Save(Path.Combine(dump, $"{k + 1:00} {c.Elevation:0}° {red}-{green}.png"), ImageFormat.Png);
                    }
                    var score = ViewPick.Score(red, green, total, c.Preference);
                    scores.Append($" {k + 1}:{red}/{green}");
                    if (score > bestScore) { bestScore = score; best = vp; bestIndex = k; }
                }
                Log.Write($"{name}: лучший — {bestIndex + 1} ({candidates[bestIndex].Elevation:0}°); красный/зелёный по направлениям:{scores}");
                // цвета — снять до записи вида (иначе красный и зелёный могли бы сохраниться в виде), остальное — пока скрыто
                doc.Models.ResetAllTemporaryMaterials();
                doc.CurrentViewpoint.CopyFrom(best);
                if (target != null)
                {
                    target.ViewPoint = (InwNvViewPoint)ComApiBridge.State.CurrentView.ViewPoint.Copy();
                    saved = true;
                }
                return true;
            }
            finally
            {
                doc.Models.ResetAllTemporaryMaterials();
                if (hidden.Count > 0) doc.Models.SetHidden(hidden, false);
            }
        }

        /// <summary>«Скрыть остальное»: всё, кроме двух элементов и их родителей. Возвращает скрытое — чтобы потом показать.</summary>
        private static ModelItemCollection HideAllBut(Document doc, ModelItem first, ModelItem second)
        {
            var keep = new HashSet<ModelItem>();
            foreach (var x in first.AncestorsAndSelf) keep.Add(x);
            foreach (var x in second.AncestorsAndSelf) keep.Add(x);
            var hide = new ModelItemCollection();
            void Walk(ModelItem node)
            {
                foreach (var child in node.Children)
                {
                    if (child.Equals(first) || child.Equals(second)) continue;
                    if (keep.Contains(child)) Walk(child);
                    else if (!child.IsHidden) hide.Add(child);
                }
            }
            foreach (var root in doc.Models.RootItems)
            {
                if (keep.Contains(root)) Walk(root);
                else if (!root.IsHidden) hide.Add(root);
            }
            if (hide.Count > 0) doc.Models.SetHidden(hide, true);
            return hide;
        }

        private static Box3 BoxOf(BoundingBox3D b) => new Box3(b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z);

        /// <summary>Сколько пикселей первого (красного) и второго (зелёного) элемента.</summary>
        private static void Count(Bitmap image, out int red, out int green, out int total)
        {
            red = green = 0;
            total = image.Width * image.Height;
            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var bytes = new byte[data.Stride * data.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                for (var y = 0; y < data.Height; y++)
                {
                    var row = y * data.Stride;
                    for (var x = 0; x < data.Width; x++)
                    {
                        var p = row + x * 3; // BGR
                        byte b = bytes[p], g = bytes[p + 1], r = bytes[p + 2];
                        if (ViewPick.IsFirst(r, g, b)) red++;
                        else if (ViewPick.IsSecond(r, g, b)) green++;
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }
        }
    }

    /// <summary>Журнал — общий с загрузчиком: %LocalAppData%\RAS_v_2.0\Logs\navisworks.log.</summary>
    internal static class Log
    {
        public static string Path => System.IO.Path.Combine(RasPaths.Logs, "navisworks.log");

        public static void Write(string text, Exception ex = null)
        {
            try
            {
                Directory.CreateDirectory(RasPaths.Logs);
                File.AppendAllText(Path, DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss") + " [Виды коллизий] " + text + Environment.NewLine +
                                         (ex != null ? ex + Environment.NewLine : ""), new UTF8Encoding(false));
            }
            catch (Exception) { /* журнал не пишется — не повод ломать Navisworks */ }
        }
    }
}
