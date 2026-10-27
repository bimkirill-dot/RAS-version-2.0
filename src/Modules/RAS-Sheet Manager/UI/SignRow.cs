using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using RASv2.SheetManager.Logic;

namespace RASv2.SheetManager.UI
{
    /// <summary>Строка списка подписей: файл, картинка, фамилия (можно поправить), что будет, размеры.</summary>
    public sealed class SignRow : INotifyPropertyChanged
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        private readonly Action _changed;
        private string _name;
        private bool _checked;
        private bool _userChecked;

        public SignRow(SignFile file, string name, string note, Action changed)
        {
            File = file;
            _name = name;
            Note = note;
            _changed = changed;
            SizeText = file.Lines != null ? string.Format(Ru, "{0:0.0} × {1:0.0} мм", file.WidthMm, file.HeightMm) : "—";
            BytesText = SignFolder.Bytes(file.SourceBytes) + " → " + (file.CleanBytes > 0 ? SignFolder.Bytes(file.CleanBytes) : "—");
            FileTip = System.IO.Path.GetFileName(file.Source) + Environment.NewLine + "→ " + System.IO.Path.GetFileName(file.Clean) +
                      (file.CleanedNow ? Environment.NewLine + "очищена сейчас" : "");
        }

        public SignFile File { get; }
        public string Note { get; }
        public string SizeText { get; }
        public string BytesText { get; }
        public string FileTip { get; }
        public ImageSource Preview { get; set; }
        public SignPlanItem Item { get; private set; }

        /// <summary>Фамилия: из имени файла, можно поправить — план пересчитывается.</summary>
        public string Name
        {
            get => _name;
            set
            {
                if (value == _name) return;
                _name = value;
                OnChanged();
                _changed?.Invoke();
            }
        }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (value == _checked) return;
                _checked = value && CanCheck;
                _userChecked = true;
                OnChanged();
                _changed?.Invoke();
            }
        }

        public bool CanCheck => Item != null && Item.Action != SignAction.Skip;
        public bool IsAdded => Item != null && Item.HasDwg && Item.Action == SignAction.Replace;
        public bool IsSkip => Item == null || Item.Action == SignAction.Skip;
        public string Status => File.Error != null ? "не очищена: " + File.Error : Item?.Status ?? "";
        public string NumberText => Item?.Number != null ? "№" + Item.Number.Value.ToString(CultureInfo.InvariantCulture) : "";

        /// <summary>Новый план для строки: отметка — своя, если человек её менял и строку всё ещё можно отметить.</summary>
        public void SetPlan(SignPlanItem item)
        {
            Item = item;
            if (File.Error != null) { item.Action = SignAction.Skip; item.Checked = false; }
            _checked = _userChecked && CanCheck ? _checked : item.Checked && CanCheck;
            item.Checked = _checked;
            OnChanged(nameof(Checked));
            OnChanged(nameof(CanCheck));
            OnChanged(nameof(IsAdded));
            OnChanged(nameof(IsSkip));
            OnChanged(nameof(Status));
            OnChanged(nameof(NumberText));
        }

        /// <summary>Номер мог появиться после нумерации отмеченных (SignPlan.Number).</summary>
        public void RefreshNumber() => OnChanged(nameof(NumberText));

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>
        /// Картинка подписи: ломаные синим в общем окне boxW × boxH мм (у всех строк одно — подписи видны в одном масштабе).
        /// </summary>
        public static ImageSource Draw(List<Polyline2> lines, double boxW, double boxH, Brush brush)
        {
            if (lines == null || lines.Count == 0) return null;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                foreach (var p in lines)
                {
                    if (p.Count < 2) continue;
                    g.BeginFigure(new Point(p.X[0], -p.Y[0]), false, p.Closed);
                    for (var i = 1; i < p.Count; i++) g.LineTo(new Point(p.X[i], -p.Y[i]), true, false);
                }
            }
            geometry.Freeze();
            var pen = new Pen(brush, Math.Max(boxW, boxH) / 120) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            var group = new DrawingGroup();
            // прозрачная рамка задаёт общий масштаб картинок
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(-boxW / 2, -boxH / 2, boxW, boxH))));
            group.Children.Add(new GeometryDrawing(null, pen, geometry));
            group.Freeze();
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }
    }
}
