namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Общая папка журналов команды. Зашита в плагин: каждый пишет сюда сразу после установки, настраивать
    /// ничего не нужно. Файл подключён и в установщик (setup) — он проверяет доступ к этой папке.
    /// Сменить папку — здесь, затем build.cmd и переустановка у всех.
    /// </summary>
    internal static class SharedFolder
    {
        public const string LogFolder = @"\\10.10.1.77\Shared\BIM\Полетаев Кирилл\RAS-Model History";
    }
}
