using System;

namespace TurboSuite.Abstractions
{
    /// <summary>
    /// Thin, Revit-free primitive for running a unit of work on Revit's API thread
    /// from a modeless (WPF) ViewModel. The shim implements this over an
    /// <c>ExternalEvent</c> draining a FIFO queue; Core ViewModels depend only on this
    /// interface plus Revit-free <i>operation</i> interfaces they invoke inside
    /// <paramref name="work"/>. This replaces the old per-tab typed-request +
    /// <c>IExternalEventHandler</c> switch with no Revit types leaking into Core.
    /// </summary>
    public interface IRevitWorkQueue
    {
        /// <summary>
        /// Queues <paramref name="work"/> to run on the Revit API thread. When it
        /// completes, <paramref name="onComplete"/> (if non-null) is invoked on the UI
        /// thread with the value <paramref name="work"/> returned. If <paramref name="work"/>
        /// throws, the shim surfaces the error and invokes <paramref name="onComplete"/>
        /// with <c>null</c>. Work enqueued from within an <paramref name="onComplete"/>
        /// callback is picked up by the same drain pass, so sequential chaining is safe.
        /// </summary>
        void Enqueue(Func<object> work, Action<object> onComplete);
    }
}
