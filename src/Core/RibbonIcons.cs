using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RASv2.Core
{
    /// <summary>
    /// Иконки кнопок — картинки PNG, вшитые в сборку модуля (или ядра): папка Icons, «&lt;имя&gt;32.png» (большая кнопка),
    /// «&lt;имя&gt;16.png» (маленькая) и тёмные «&lt;имя&gt;32.dark.png», «&lt;имя&gt;16.dark.png»; ресурс — «Icons.&lt;файл&gt;»
    /// (EmbeddedResource с LogicalName в проекте). Картинки делает tools\IconBuilder (icons.cmd) — единый стиль.
    /// Тёмные — когда лента Revit тёмная (Revit 2024 и новее); при смене темы ядро меняет иконки (Host.OnThemeChanged).
    /// Отдельные файлы рядом с DLL не нужны; адресов pack:// нет — в плагине Revit они ненадёжны.
    /// </summary>
    internal static class RibbonIcons
    {
        /// <summary>Чья иконка у кнопки: код кнопки → сборка и имя иконки — чтобы сменить иконки при смене темы.</summary>
        private static readonly Dictionary<string, KeyValuePair<Assembly, string>> Owners =
            new Dictionary<string, KeyValuePair<Assembly, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Лента Revit тёмная. В Revit 2023 тёмной ленты нет.</summary>
        public static bool IsDark
        {
            get
            {
#if REVIT2023
                return false;
#else
                try { return UIThemeManager.CurrentTheme == UITheme.Dark; }
                catch (Exception) { return false; } // тема недоступна — светлые иконки, кнопки всё равно работают
#endif
            }
        }

        /// <summary>Иконки кнопки по текущей теме; запоминается, чья иконка, — для смены темы.</summary>
        public static void Set(ButtonData data, Assembly assembly, string name)
        {
            Owners[data.Name] = new KeyValuePair<Assembly, string>(assembly, name);
            var dark = IsDark;
            data.LargeImage = Load(assembly, name, 32, dark);
            data.Image = Load(assembly, name, 16, dark);
        }

        /// <summary>
        /// Тема Revit сменилась: иконки кнопки — по новой теме (у кнопок без иконки — ничего). Переключатель отделов
        /// (выпадающая кнопка на месте «О плагине») — с иконкой «О плагине».
        /// </summary>
        public static void Refresh(string id, RibbonItem item)
        {
            if (!Owners.TryGetValue(id, out var owner)) return;
            var dark = IsDark;
            var large = Load(owner.Key, owner.Value, 32, dark);
            var small = Load(owner.Key, owner.Value, 16, dark);
            if (item is PushButton push)
            {
                push.LargeImage = large;
                push.Image = small;
            }
            else if (item is PulldownButton pulldown)
            {
                pulldown.LargeImage = large;
                pulldown.Image = small;
            }
        }

        /// <summary>Иконка размера size (32 или 16); тёмной нет — светлая; нет и светлой — null, причина в журнале.</summary>
        private static ImageSource Load(Assembly assembly, string name, int size, bool dark)
        {
            if (dark)
            {
                var image = Read(assembly, "Icons." + name + size + ".dark.png", quiet: true);
                if (image != null) return image;
            }
            return Read(assembly, "Icons." + name + size + ".png", quiet: false);
        }

        private static ImageSource Read(Assembly assembly, string resource, bool quiet)
        {
            try
            {
                using (var stream = assembly.GetManifestResourceStream(resource))
                {
                    if (stream == null)
                    {
                        if (!quiet) Log.Write("Ядро", "Нет иконки " + resource + " в " + assembly.GetName().Name);
                        return null;
                    }
                    return Decode(stream);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Ядро", "Не удалось загрузить иконку " + resource + " из " + assembly.GetName().Name, ex);
                return null;
            }
        }

        /// <summary>
        /// Картинка с разрешением 96 точек на дюйм: лента Revit берёт размер в единицах WPF, и PNG с другим разрешением
        /// (у картинок из редакторов бывает 72) вышла бы крупнее кнопки и размытой.
        /// </summary>
        private static ImageSource Decode(Stream stream)
        {
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource image = frame;
            if (Math.Abs(frame.DpiX - 96) > 0.5 || Math.Abs(frame.DpiY - 96) > 0.5)
            {
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var stride = converted.PixelWidth * 4;
                var pixels = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(pixels, stride, 0);
                image = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            }
            image.Freeze();
            return image;
        }
    }
}
