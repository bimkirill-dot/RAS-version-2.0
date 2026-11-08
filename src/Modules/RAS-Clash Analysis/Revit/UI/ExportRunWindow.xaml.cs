using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RASv2.ClashAnalysis.Export;
using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>
    /// Ход выгрузки (DECISIONS.md модуля, «Окно хода — снова»): NWC и NWF запуска, у каждой — состояние, шаг, время; «готово N из M»;
    /// «Отмена» — после текущей модели; в конце — итог, журнал, папка. Было убрано 07.11.2026 (ход — в списке задач) и в тот же день
    /// возвращено (разработчик: «там удобно отображать и статус, и время, и ход выполнения»); в списке задач остались итог прошлого
    /// запуска (значки у галочек), «Запуск» и «Время». Работа идёт в потоке Revit (Revit API — только там), окно обновляется между
    /// шагами: пока Revit открывает или выгружает модель, окно не перерисовывается — так и должно быть.
    /// </summary>
    public partial class ExportRunWindow : Window, IRunView
    {
        /// <summary>Строка окна: NWC или NWF и её состояние.</summary>
        public sealed class RunRow : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            public NwcProgress Progress { get; set; }
            public int Number { get; set; }
            public string Name => Progress.Job.ModelName;
            public NwcState State => Progress.State;
            public string StateName => Progress.Job.IsNwf && Progress.State == NwcState.Done ? "готова" : NwcExporter.StateText(Progress.State);
            public string Detail => Progress.State == NwcState.Working || Progress.State == NwcState.Waiting
                ? Progress.Step + "  →  " + Progress.Job.NwcPath
                : Progress.Note ?? Progress.Step;
            public string TimeText => Progress.Time > TimeSpan.Zero ? NwcHistory.Duration(Progress.Time.TotalSeconds) : "";

            public void Update() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        private List<RunRow> _rows = new List<RunRow>();
        private readonly Func<ExportRunWindow, bool> _run;
        private string _journal;
        private string _folder;
        private readonly Stopwatch _clock = new Stopwatch();
        private bool _started;
        private bool _running;

        /// <summary>«Отмена» нажата — после текущей модели остальные пропускаются.</summary>
        public bool Cancelled { get; private set; }

        /// <param name="run">Выгрузка; false — не началась (причина уже показана) — окно закрывается само.</param>
        public ExportRunWindow(string title, Func<ExportRunWindow, bool> run)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            Title = "Выгрузка — " + title;
            _run = run;
            Head.Text = "Выгрузка — " + title;
            Sub.Text = "Готовится план выгрузки…";
            LogButton.Visibility = Visibility.Collapsed;
            Header();
            Closing += (s, e) =>
            {
                if (!_running) return;
                e.Cancel = true;
                Cancel_Click(null, null);
            };
        }

        /// <summary>Что пойдёт в запуск — строками окна; журнал и папка — для кнопок.</summary>
        public void SetItems(IList<NwcProgress> items, string journal)
        {
            _rows = items.Select((p, i) => new RunRow { Progress = p, Number = i + 1 }).ToList();
            RowsList.ItemsSource = _rows;
            _journal = journal;
            _folder = items.Select(p => Path.GetDirectoryName(p.Job.NwcPath)).FirstOrDefault();
            Sub.Text = "Revit занят выгрузкой: окно обновляется между шагами. Журнал пишется сразу — " + journal;
            LogButton.Visibility = Visibility.Visible;
            Header();
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }

        /// <summary>
        /// Выгрузить — сразу, в вызове от Revit (окно открыто Show, не ShowDialog: тот выключает и Revit — разработчик, 08.11.2026: «хочу
        /// сворачивать Revit»). Кончилась — окно с итогом остаётся, Revit свободен; не началась (причина показана) — окно закрывается.
        /// </summary>
        public void RunNow()
        {
            if (_started) return;
            _started = true;
            // окно — нарисовать до работы
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Work();
        }

        private void Work()
        {
            _running = true;
            _clock.Start();
            var started = false;
            try
            {
                started = _run(this);
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Выгрузка: запуск оборвался", ex);
                MessageBox.Show(this, "Выгрузка оборвалась: " + ex.Message + "\nПодробности — в журнале выгрузки и журнале плагина.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                started = _rows.Count > 0;
            }
            finally
            {
                _running = false;
                _clock.Stop();
            }
            // выгружать нечего (причина уже показана) — окно не нужно
            if (!started)
            {
                Close();
                return;
            }
            Finished();
        }

        /// <summary>Строка изменилась — показать и дать окну перерисоваться (и принять «Отмену»).</summary>
        public void Changed(NwcProgress p)
        {
            var row = _rows.FirstOrDefault(r => r.Progress == p);
            row?.Update();
            Header();
            // текущая — в видимой части списка
            if (row != null && p.State == NwcState.Working && RowsList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement item)
                item.BringIntoView();
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }

        /// <summary>Подождать, не замирая: окно перерисовывается и принимает «Отмену» (пока ждём фоновый Revit или Navisworks).</summary>
        public void Pump(int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                Thread.Sleep(30);
            }
            Header();
        }

        private void Header()
        {
            var done = _rows.Count(r => r.State == NwcState.Done);
            var failed = _rows.Count(r => r.State == NwcState.Failed);
            var skipped = _rows.Count(r => r.State == NwcState.Skipped);
            var finished = done + failed + skipped;
            Foot.Text = $"готово {finished} из {_rows.Count}: без ошибок {done}, с ошибкой {failed}, пропущено {skipped}" +
                        (_clock.Elapsed > TimeSpan.Zero ? " · " + NwcHistory.Duration(_clock.Elapsed.TotalSeconds) : "") +
                        (Cancelled && _running ? " · остановится после текущей модели" : "");
        }

        private void Finished()
        {
            Header();
            var failed = _rows.Count(r => r.State == NwcState.Failed);
            Head.Text = (Cancelled ? "Выгрузка остановлена" : failed > 0 ? "Выгрузка закончена с ошибками" : "Выгрузка закончена") +
                        $" — {NwcHistory.Duration(_clock.Elapsed.TotalSeconds)}";
            Sub.Text = "Журнал: " + _journal;
            CancelButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
            FolderButton.Visibility = Directory.Exists(_folder ?? "") ? Visibility.Visible : Visibility.Collapsed;
            // Revit мог быть свёрнут, пока шла выгрузка — мигнуть
            Activate();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (Cancelled) return;
            if (MessageBox.Show(this, "Остановить выгрузку после текущей модели?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Cancelled = true;
            CancelButton.IsEnabled = false;
            Header();
        }

        private void Log_Click(object sender, RoutedEventArgs e) => Open(_journal);

        private void Folder_Click(object sender, RoutedEventArgs e) => Open(_folder);

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Open(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не открылось: " + path + "\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
