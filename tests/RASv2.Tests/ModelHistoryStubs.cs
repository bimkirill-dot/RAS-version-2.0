namespace RASv2.ModelHistory.Storage
{
    /// <summary>
    /// Журнал ошибок модуля для проверок: настоящий Diag пишет в журнал ядра (Revit API), здесь — в никуда.
    /// </summary>
    public static class Diag
    {
        public static void Log(string message, System.Exception ex = null)
        {
        }
    }
}
