using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.UI
{
    /// <summary>
    /// Столбцы параметров модели в таблице «Изменения»: правый щелчок по заголовку → «Добавить столбец параметра…»;
    /// их может быть несколько, набор — MainViewModel.ParamColumns (запоминается в ui.json), ширина и место — как у
    /// остальных столбцов (ключ «Param:&lt;имя&gt;»). Щелчок по заголовку такого столбца — сортировка по параметру.
    /// И таблица во всю ширину: последний видимый столбец занимает оставшееся место.
    /// </summary>
    public partial class SpyPane
    {
        private const string ParamKeyPrefix = "Param:";
        private static readonly StringComparer NameComparer = StringComparer.CurrentCultureIgnoreCase;

        /// <summary>Столбец таблицы → имя параметра.</summary>
        private readonly Dictionary<DataGridColumn, string> _paramColumns = new Dictionary<DataGridColumn, string>();

        /// <summary>Столбцы параметров из ui.json — до восстановления ширины и порядка столбцов.</summary>
        private void InitParamColumns()
        {
            foreach (var name in _vm.ParamColumns) AddParamGridColumn(name);
            _vm.ParamColumns.CollectionChanged += (s, e) => SyncParamColumns();
        }

        private void AddParamGridColumn(string name)
        {
            var column = new DataGridTextColumn
            {
                Header = name,
                Binding = new Binding(".") { Mode = BindingMode.OneWay, Converter = ParamCellConverter.Instance, ConverterParameter = name },
                Width = new DataGridLength(170),
                MinWidth = 60,
                IsReadOnly = true,
                CanUserSort = true,
                SortMemberPath = ParamKeyPrefix + name // сортирует модель представления — путь нужен, чтобы щелчок дошёл до неё
            };
            LogGrid.Columns.Add(column);
            _columnDefaults[column] = (column.Width, LogGrid.Columns.Count - 1);
            _paramColumns[column] = name;
            _columns.Add((ParamKeyPrefix + name, name, column, true));
            HookColumnWidth(column);
        }

        /// <summary>Набор столбцов параметров изменился (добавили, убрали) — таблица следом.</summary>
        private void SyncParamColumns()
        {
            foreach (var kv in _paramColumns.Where(kv => !_vm.ParamColumns.Contains(kv.Value, NameComparer)).ToList())
            {
                LogGrid.Columns.Remove(kv.Key);
                _paramColumns.Remove(kv.Key);
                _columnDefaults.Remove(kv.Key);
                _columns.RemoveAll(c => ReferenceEquals(c.Column, kv.Key));
            }
            foreach (var name in _vm.ParamColumns)
                if (!_paramColumns.Values.Contains(name, NameComparer)) AddParamGridColumn(name);
            UpdateSortGlyphs();
            QueueFill();
            QueueSave();
        }

        /// <summary>«Добавить столбец параметра…»: окно со всеми параметрами модели и поиском.</summary>
        private void AskParamColumn()
        {
            var names = _vm.ParamNames.Where(n => !_vm.ParamColumns.Contains(n, NameComparer)).ToList();
            if (names.Count == 0)
            {
                _vm.Status = "Список параметров ещё пуст: он собирается, пока модель запоминается (внизу слева). Попробуйте чуть позже.";
                return;
            }
            try
            {
                var win = new ParamPickerWindow(names);
                OwnByRevit(win);
                if (win.ShowDialog() == true && win.Chosen != null) _vm.AddParamColumn(win.Chosen);
            }
            catch (Exception ex)
            {
                Diag.Log("Param picker failed", ex);
                _vm.Status = "Не удалось открыть список параметров: " + ex.Message;
            }
        }

        /// <summary>Ширина столбца запоминается; после ручного изменения таблица снова растягивается во всю ширину.</summary>
        private void HookColumnWidth(DataGridColumn column)
        {
            var widthProperty = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
            var last = (column.Width.UnitType, column.Width.Value);
            widthProperty?.AddValueChanged(column, (s, e) =>
            {
                var now = (column.Width.UnitType, column.Width.Value);
                if (now == last) return;
                last = now;
                QueueSave();
                QueueFill();
            });
        }

        private bool _fillQueued;

        private void QueueFill()
        {
            if (_fillQueued) return;
            _fillQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _fillQueued = false;
                EnsureFill();
            }));
        }

        /// <summary>
        /// Таблица во всю ширину: растягивается «Что изменилось», но после того как его ширину поменяли мышью, WPF делает её
        /// постоянной, и справа остаётся пустое место. Тогда растягивается последний видимый столбец.
        /// </summary>
        private void EnsureFill()
        {
            var visible = LogGrid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).ToList();
            if (visible.Count == 0 || visible.Any(c => c.Width.IsStar)) return;
            var fill = visible[visible.Count - 1];
            if (ReferenceEquals(fill, ColStatus) && _statusForced) return; // «Причина» при актуализации — своей ширины
            fill.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        }
    }

    /// <summary>Ячейка столбца параметра: значение параметра из строки (ChangeRow.ParamValueOf).</summary>
    public sealed class ParamCellConverter : IValueConverter
    {
        public static readonly ParamCellConverter Instance = new ParamCellConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is ChangeRow row ? row.ParamValueOf(parameter as string) : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
