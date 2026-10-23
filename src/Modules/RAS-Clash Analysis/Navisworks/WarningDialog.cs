using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RASv2.ClashAnalysis.Navisworks
{
    /// <summary>
    /// Окно с заметным предупреждением — красный круг с восклицательным знаком и красный текст (разработчик, 23.10.2026: «прям такое
    /// предупреждение с восклицательным знаком красным, чтобы пользователь точно не пропустил»): стандартное окно Windows с жёлтым
    /// треугольником пропускают. Кнопки — с понятными подписями; результат — номер нажатой (−1 — закрыли окно).
    /// </summary>
    internal sealed class WarningDialog : Form
    {
        private static readonly Color Red = Color.FromArgb(200, 30, 30);

        public int Choice { get; private set; } = -1;

        private WarningDialog(string warning, string text, string[] buttons)
        {
            Text = Entry.Title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;
            const int width = 560;

            var icon = new Panel { Location = new Point(18, 18), Size = new Size(48, 48) };
            icon.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var fill = new SolidBrush(Red)) g.FillEllipse(fill, 1, 1, 45, 45);
                using (var white = new SolidBrush(Color.White))
                {
                    g.FillRectangle(white, 20.5f, 9, 6, 21);
                    g.FillEllipse(white, 20.5f, 33, 6, 6);
                }
            };
            // высота подписей — их собственная (AutoSize с ограничением ширины): расчёт заранее обрезал последнюю строку
            var head = new Label
            {
                Text = warning, ForeColor = Red, Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                Location = new Point(80, 16), AutoSize = true, MaximumSize = new Size(width - 100, 0),
            };
            var body = new Label { Text = text, AutoSize = true, MaximumSize = new Size(width - 100, 0) };
            Controls.Add(head);
            Controls.Add(body);
            body.Location = new Point(80, Math.Max(icon.Bottom, head.Bottom) + 14);

            var buttonsTop = body.Bottom + 20;
            var strip = new Panel { BackColor = Color.FromArgb(240, 240, 240), Location = new Point(0, buttonsTop - 10), Size = new Size(width, 56) };
            var right = width - 14;
            for (var i = buttons.Length - 1; i >= 0; i--)
            {
                var index = i;
                var button = new Button { Text = buttons[i], AutoSize = true, MinimumSize = new Size(96, 30), Padding = new Padding(8, 0, 8, 0) };
                button.Click += (s, e) => { Choice = index; Close(); };
                strip.Controls.Add(button);
                var size = button.GetPreferredSize(Size.Empty);
                button.Size = new Size(Math.Max(96, size.Width), 30);
                right -= button.Width;
                button.Location = new Point(right, 13);
                right -= 8;
                if (i == 0) AcceptButton = button;
            }
            Controls.Add(icon);
            Controls.Add(strip);
            ClientSize = new Size(width, strip.Bottom);
        }

        /// <summary>Показать; вернуть номер нажатой кнопки (−1 — закрыли крестиком).</summary>
        public static int Show(string warning, string text, params string[] buttons)
        {
            using (var dialog = new WarningDialog(warning, text, buttons))
            {
                dialog.ShowDialog();
                return dialog.Choice;
            }
        }
    }
}
