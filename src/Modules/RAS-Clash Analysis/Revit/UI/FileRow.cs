using RASv2.ClashAnalysis.Logic;

namespace RASv2.ClashAnalysis.UI
{
    /// <summary>Файл в списке (файлы проверок): имя — первым, папка — после него бледным, полный путь — в подсказке.</summary>
    public sealed class FileRow
    {
        public FileRow(string path) => Path = path;

        public string Path { get; }
        public string Name => ExportNodes.FileName(Path);
        public string Folder
        {
            get
            {
                var name = Name;
                return Path.Length > name.Length ? Path.Substring(0, Path.Length - name.Length).TrimEnd('\\', '/') : "";
            }
        }
    }
}
