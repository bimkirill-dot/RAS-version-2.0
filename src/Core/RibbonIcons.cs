using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RASv2.Core
{
    /// <summary>
    /// Иконки кнопок — картинки PNG, вшитые в сборку модуля (или ядра): папка Icons, файлы «&lt;имя&gt;32.png» (большая
    /// кнопка) и «&lt;имя&gt;16.png» (маленькая), ресурс — «Icons.&lt;файл&gt;» (EmbeddedResource с LogicalName в проекте).
    /// Отдельные файлы рядом с DLL не нужны; адресов pack:// нет — в плагине Revit они ненадёжны.
    /// </summary>
    internal static class RibbonIcons
    {
        /// <summary>Иконка размера size (32 или 16); нет такой — null, причина в журнале: кнопка остаётся без иконки.</summary>
        public static ImageSource Load(Assembly assembly, string name, int size)
        {
            var resource = "Icons." + name + size + ".png";
            try
            {
                using (var stream = assembly.GetManifestResourceStream(resource))
                {
                    if (stream == null)
                    {
                        Log.Write("Ядро", "Нет иконки " + resource + " в " + assembly.GetName().Name);
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
