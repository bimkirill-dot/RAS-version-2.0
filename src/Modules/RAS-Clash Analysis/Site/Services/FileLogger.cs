using System.Text;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>
/// Журнал сайта — файл на машине сайта (PRINCIPLES.md, раздел 7): «Журнал\site-ГГГГ-ММ.log» в папке данных сайта.
/// Пишет то, что пропускают настройки Logging в appsettings.json.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly object _sync = new object();

    public FileLoggerProvider(string folder) => _folder = folder;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception ex)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder();
        sb.Append(now.ToString("dd.MM.yyyy HH:mm:ss")).Append(' ').Append(Level(level)).Append(' ')
            .Append(category.Substring(category.LastIndexOf('.') + 1)).Append(": ").Append(message);
        if (ex != null) sb.AppendLine().Append(ex);
        sb.AppendLine();
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_folder);
                File.AppendAllText(Path.Combine(_folder, $"site-{now:yyyy-MM}.log"), sb.ToString(), Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // журнал недоступен — сайт работает дальше; в окне сайта сообщения всё равно видны
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Critical => "СБОЙ  ",
        LogLevel.Error => "ОШИБКА",
        LogLevel.Warning => "ВНИМ. ",
        _ => "      ",
    };

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _owner;
        private readonly string _category;

        public FileLogger(FileLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (IsEnabled(logLevel)) _owner.Write(_category, logLevel, formatter(state, exception), exception);
        }
    }
}
