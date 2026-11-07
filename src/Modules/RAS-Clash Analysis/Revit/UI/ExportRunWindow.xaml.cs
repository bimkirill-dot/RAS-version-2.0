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
    /// Ход выгрузки NWC: работа идёт в потоке Revit (Revit API — только там), окно обновляется между шагами; пока Revit открывает
    /// или выгружает модель, окно не перерисовывается — так и должно быть. «Отмена» — после текущей модели.
    /// </summary>
    public partial class ExportRunWindow : Window
    {
        /// <summary>Строка окна: NWC и её состояние.</summary>
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

        private readonly List<RunRow> _rows;
        private readonly Action<ExportRunWindow> _run;
        private readonly string _journal;
        private readonly string _nwcFolder;
        private readonly Stopwatch _clock = new Stopwatch();
        private bool _started;
        private bool _running;

        /// <summary>«Отмена» нажата — после текущей модели остальные пропускаются.</summary>
        public bool Cancelled { get; private set; }

        public ExportRunWindow(string title, IList<NwcProgress> items, string journal, string nwcFolder, Action<ExportRunWindow> run)
        {
            RASv2.Core.WindowTheme.Apply(this);
            InitializeComponent();
            Title = "Выгрузка NWC — " + title;
            _rows = items.Select((p, i) => new RunRow { Progress = p, Number = i + 1 }).ToList();
            _run = run;
            _journal = journal;
            _nwcFolder = nwcFolder;
            RowsList.ItemsSource = _rows;
            Head.Text = $"Выгрузка NWC — {title}";
            Sub.Text = "Revit занят выгрузкой: окно обновляется между шагами. Журнал пишется сразу — " + journal;
            Header();
            // работа — после показа окна: иначе окно не появится, пока не выгрузится всё
            ContentRendered += (s, e) =>
            {
                if (_started) return;
                _started = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Work));
            };
            Closing += (s, e) =>
            {
                if (!_running) return;
                e.Cancel = true;
                Cancel_Click(null, null);
            };
        }

        private void Work()
        {
            _running = true;
            _clock.Start();
            try
            {
                _run(this);
            }
            catch (Exception ex)
            {
                RASv2.Core.Log.Write("ClashAnalysis", "Выгрузка NWC: запуск оборвался", ex);
                MessageBox.Show(this, "Выгрузка оборвалась: " + ex.Message + "\nПодробности — в журнале выгрузки и журнале плагина.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _running = false;
                _clock.Stop();
                Finished();
            }
        }

        /// <summary>Строка изменилась — показать и дать окну перерисоваться (и принять «Отмену»).</summary>
        public void Changed(NwcProgress p)
        {
            var row = _rows.FirstOrDefault(r => r.Progress == p);
            row?.Update();
            Header();
            // текущая модель — в видимой части списка
            if (row != null && p.State == NwcState.Working && RowsList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement item)
                item.BringIntoView();
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }

        /// <summary>Подождать, не замирая: окно перерисовывается и принимает «Отмену» (пока ждём фоновый Revit).</summary>
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
            Foot.Text = $"готово {finished} из {_rows.Count}: выгружено {done}, с ошибкой {failed}, пропущено {skipped}" +
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
            FolderButton.Visibility = Directory.Exists(_nwcFolder ?? "") ? Visibility.Visible : Visibility.Collapsed;
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

        private void Folder_Click(object sender, RoutedEventArgs e) => Open(_nwcFolder);

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Open(string path)
        {
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
