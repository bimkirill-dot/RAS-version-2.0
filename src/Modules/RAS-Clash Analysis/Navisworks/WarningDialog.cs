using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RASv2.ClashAnalysis.Navisworks
{
    /// <summary>
    /// Окно с заметным предупреждением — как обычное окно Windows (системный шрифт, серая полоса с кнопками справа), но с красным
    /// значком «!» и красной строкой-предупреждением (разработчик, 23.10.2026: «предупреждение с восклицательным знаком красным,
    /// чтобы пользователь точно не пропустил»; затем — «попроще, под стандартный интерфейс Windows, просто оставить заметное
    /// предупреждение»). Стандартное окно Windows красного «!» не умеет (у него жёлтый треугольник). Результат — номер нажатой кнопки
    /// (−1 — закрыли окно).
    /// </summary>
    internal sealed class WarningDialog : Form
    {
        private static readonly Color Red = Color.FromArgb(196, 43, 28);

        public int Choice { get; private set; } = -1;

        private WarningDialog(string warning, string text, string[] buttons)
        {
            Text = Entry.Title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            BackColor = SystemColors.Window;
            const int textLeft = 66, textWidth = 420;

            // значок — размера системного (32), красный круг с белым «!»
            var icon = new Panel { Location = new Point(20, 22), Size = new Size(32, 32) };
            icon.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var fill = new SolidBrush(Red)) g.FillEllipse(fill, 0, 0, 31, 31);
                using (var white = new SolidBrush(Color.White))
                {
                    g.FillRectangle(white, 13.5f, 6, 4, 14);
                    g.FillEllipse(white, 13.5f, 22.5f, 4, 4);
                }
            };
            var head = new Label
            {
                Text = warning, ForeColor = Red, Font = new Font(Font, FontStyle.Bold),
                AutoSize = true, MaximumSize = new Size(textWidth, 0), Location = new Point(textLeft, 22),
            };
            var body = new Label { Text = text, AutoSize = true, MaximumSize = new Size(textWidth, 0) };
            Controls.Add(icon);
            Controls.Add(head);
            Controls.Add(body);
            body.Location = new Point(textLeft, head.Bottom + 10);

            // полоса кнопок — как у окон Windows: серая, кнопки справа, одинаковой высоты, с промежутками
            var strip = new Panel { BackColor = SystemColors.Control, Location = new Point(0, Math.Max(icon.Bottom, body.Bottom) + 22) };
            var width = textLeft + textWidth + 24;
            var right = width - 12;
            for (var i = buttons.Length - 1; i >= 0; i--)
            {
                var index = i;
                var button = new Button { Text = buttons[i], AutoSize = false, UseVisualStyleBackColor = true };
                var w = Math.Max(88, TextRenderer.MeasureText(buttons[i], Font).Width + 24);
                button.Size = new Size(w, 26);
                right -= w;
                button.Location = new Point(right, 12);
                right -= 8;
                button.Click += (s, e) => { Choice = index; Close(); };
                strip.Controls.Add(button);
                if (i == 0) AcceptButton = button;
                if (i == buttons.Length - 1) CancelButton = button;
            }
            strip.Size = new Size(width, 50);
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
