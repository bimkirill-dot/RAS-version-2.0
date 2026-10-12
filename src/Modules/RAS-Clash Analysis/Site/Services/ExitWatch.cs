using System.Runtime.InteropServices;

namespace RASv2.ClashAnalysis.Site.Services;

/// <summary>
/// Почему сайт завершился — сразу в журнал: окно закрыто, выход из Windows, сбой, обычная остановка.
/// 09.10 и 12.10.2026 сайт пропадал без единой записи; нет ни одной из этих записей — процесс завершили снаружи
/// (диспетчер задач, site.cmd пишет о себе сам, «задание» Windows того, кто запустил).
/// </summary>
public static class ExitWatch
{
    private delegate bool ConsoleHandler(int type);

    /// <summary>Ссылка на делегат — иначе сборщик мусора уберёт его, пока Windows его помнит.</summary>
    private static ConsoleHandler _handler;

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(ConsoleHandler handler, bool add);

    public static void Start(ILogger log)
    {
        _handler = type =>
        {
            log.LogWarning("Окно сайта: {Event} — сайт останавливается", type switch
            {
                0 => "Ctrl+C",
                1 => "Ctrl+Break",
                2 => "окно закрыто",
                5 => "выход из Windows",
                6 => "выключение Windows",
                _ => "событие " + type,
            });
            return false; // дальше — обычная остановка
        };
        SetConsoleCtrlHandler(_handler, true);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.LogCritical(e.ExceptionObject as Exception, "Сбой сайта — он закрывается");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => log.LogInformation("Сайт завершён.");
    }
}
