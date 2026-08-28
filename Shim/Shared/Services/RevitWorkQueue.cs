#nullable disable
using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Shim-side implementation of <see cref="IRevitWorkQueue"/> over an
    /// <see cref="ExternalEvent"/>. <c>Enqueue</c> is called on the WPF/UI thread and
    /// raises the event; <c>Execute</c> runs on Revit's API thread and drains the whole
    /// FIFO in a loop, dispatching each completion callback synchronously onto the UI
    /// thread. Because callbacks run synchronously inline, any work they enqueue is
    /// picked up by the same drain pass — so sequential chaining works without re-raising
    /// the event (the old "never raise twice" footgun does not apply here).
    ///
    /// Shared shim infrastructure — both TurboNumber and TurboZones modeless commands
    /// drive their Core ViewModels through one instance of this queue.
    /// </summary>
    public class RevitWorkQueue : IExternalEventHandler, IRevitWorkQueue, IDisposable
    {
        private readonly object _lock = new object();
        private readonly Queue<(Func<object> Work, Action<object> OnComplete)> _queue
            = new Queue<(Func<object>, Action<object>)>();
        private readonly string _errorTitle;
        private readonly string _name;
        private ExternalEvent _externalEvent;

        // UI-thread dispatcher captured at construction (the command builds this on Revit's API/UI thread — the
        // same thread the modeless window lives on). Completion callbacks marshal through this, NOT through
        // System.Windows.Application.Current: on Revit 2024 there is no WPF Application, so Application.Current is
        // null and dispatching through it silently drops the callback (spike-confirmed null on 2024, present on
        // 2025/2026). Unlike TurboName's single-shot latch this queue can't hard-lock from a dropped callback,
        // but the UI would still miss its post-op refresh — same root cause, fixed the same way.
        private readonly Dispatcher _dispatcher;

        public RevitWorkQueue(string errorTitle, string name = "TurboSuite Work Queue")
        {
            _errorTitle = errorTitle;
            _name = name;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _externalEvent = ExternalEvent.Create(this);
        }

        public void Enqueue(Func<object> work, Action<object> onComplete)
        {
            lock (_lock)
            {
                _queue.Enqueue((work, onComplete));
            }
            _externalEvent.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (true)
            {
                (Func<object> Work, Action<object> OnComplete) item;
                lock (_lock)
                {
                    if (_queue.Count == 0) return;
                    item = _queue.Dequeue();
                }

                object result = null;
                try
                {
                    result = item.Work?.Invoke();
                }
                catch (Exception ex)
                {
                    TaskDialog.Show(_errorTitle, $"An error occurred:\n{ex.Message}");
                    result = null;
                }

                if (item.OnComplete != null)
                {
                    var callback = item.OnComplete;
                    var captured = result;
                    // Invoke (not BeginInvoke) to keep the documented synchronous-inline drain: Execute runs on
                    // the dispatcher's own thread, so this runs the callback right here before the next item.
                    if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
                        _dispatcher.Invoke(() => callback(captured));
                    else
                        callback(captured);
                }
            }
        }

        public string GetName() => _name;

        public void Dispose()
        {
            _externalEvent?.Dispose();
            _externalEvent = null;
        }
    }
}
