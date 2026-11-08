using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace RASv2.ClashAnalysis.Export
{
    /// <summary>
    /// Вызовы Revit API из немодального окна «Менеджер задач» (разработчик, 08.11.2026: «чтобы с Revit можно было взаимодействовать,
    /// пока открыто окно плагина»): Revit API работает только внутри вызова от Revit — окно ставит действие в очередь, внешнее
    /// событие выполняет его, когда Revit свободен. Создаётся внутри команды (там создать внешнее событие можно).
    /// </summary>
    internal sealed class RevitCalls : IExternalEventHandler
    {
        private readonly Queue<Action<UIApplication>> _calls = new Queue<Action<UIApplication>>();
        private readonly ExternalEvent _event;
        private readonly Action<string, Exception> _error;

        public RevitCalls(Action<string, Exception> error)
        {
            _error = error;
            _event = ExternalEvent.Create(this);
        }

        public void Post(Action<UIApplication> call)
        {
            lock (_calls) _calls.Enqueue(call);
            _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (true)
            {
                Action<UIApplication> call;
                lock (_calls)
                {
                    if (_calls.Count == 0) return;
                    call = _calls.Dequeue();
                }
                try
                {
                    call(app);
                }
                catch (Exception ex)
                {
                    // ошибка сквозь внешнее событие уронила бы Revit
                    _error("Менеджер задач: действие в Revit не выполнено", ex);
                }
            }
        }

        public string GetName() => "RAS-Clash Analysis: Менеджер задач";
    }
}
