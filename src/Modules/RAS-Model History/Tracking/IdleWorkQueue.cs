using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RASv2.ModelHistory.Storage;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>
    /// Фоновая работа, которая требует Revit API (кэширование элементов, снимки параметров,
    /// разбор изменений после синхронизации). Выполняется порциями в событии Idling,
    /// чтобы Revit не «подвисал».
    /// </summary>
    public sealed class IdleWorkQueue
    {
        /// <summary>Порция работы. Возвращает true, когда задача полностью выполнена.</summary>
        public interface IJob
        {
            bool Step(UIApplication app, Stopwatch clock, long budgetMs);
            /// <summary>Задача больше не нужна (например, документ закрыт).</summary>
            bool IsObsolete { get; }
        }

        private readonly LinkedList<IJob> _jobs = new LinkedList<IJob>();
        /// <summary>Порция работы за один «простой» Revit: короткая, пока человек работает.</summary>
        private const long ActiveBudgetMs = 40;
        /// <summary>…и крупная, когда мышь и клавиатура не трогались больше 1,5 секунды.</summary>
        private const long IdleBudgetMs = 100;

        public int Count => _jobs.Count;

        public void Enqueue(IJob job) => _jobs.AddLast(job);

        public void EnqueueFirst(IJob job) => _jobs.AddFirst(job);

        public void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_jobs.Count == 0) return;
            var app = sender as UIApplication;
            var clock = Stopwatch.StartNew();
            var budgetMs = UserActivity.IsIdle ? IdleBudgetMs : ActiveBudgetMs;
            try
            {
                while (_jobs.Count > 0 && clock.ElapsedMilliseconds < budgetMs)
                {
                    var job = _jobs.First.Value;
                    bool done;
                    try
                    {
                        done = job.IsObsolete || job.Step(app, clock, budgetMs);
                    }
                    catch (Exception ex)
                    {
                        Diag.Log("Idle job failed: " + job.GetType().Name, ex);
                        done = true;
                    }
                    if (!done) break; // задача отдала свою порцию (короткую, пока человек работает) — продолжит в следующий простой
                    if (_jobs.Count > 0 && ReferenceEquals(_jobs.First.Value, job)) _jobs.RemoveFirst();
                    else _jobs.Remove(job);
                }
            }
            finally
            {
                if (_jobs.Count > 0) e.SetRaiseWithoutDelay();
            }
        }
    }
}
