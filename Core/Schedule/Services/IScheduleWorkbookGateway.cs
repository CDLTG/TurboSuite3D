#nullable disable
using System;
using System.Collections.Generic;
using TurboSuite.Schedule.Models;

namespace TurboSuite.Schedule.Services;

/// <summary>
/// VM-facing seam for the workbook round-trip, mirroring how <c>ScheduleMainViewModel</c> already gets
/// <c>IScheduleWriter</c> + <c>IRevitWorkQueue</c>. The shim implementation
/// (<c>ScheduleWorkbookGateway</c>) owns everything Revit/dialog/file: path resolution (Save-As on first
/// use, stored per project), lock pre-flight, wrong-project warn-and-allow, collect-on-the-API-thread, and
/// the ClosedXML read/write. The VM only sees Core DTOs and callbacks.
/// </summary>
public interface IScheduleWorkbookGateway
{
    /// <summary>
    /// One bidirectional reconcile (the single "Sync workbook" button). When a workbook already exists it
    /// pulls the designer's edits into the model (workbook → model), then refreshes the workbook from the
    /// now-current model (append new Type Marks, flag/purge removed ones). On first run — or if the stored
    /// file is gone — it instead just seeds/recreates the workbook (Save-As, no pull); the result is then
    /// <see cref="ReconcileResult.SeededOnly"/>.
    ///
    /// <para>Callbacks land on the WPF thread; exactly one fires. <paramref name="onCancelled"/> = the user
    /// dismissed the Save-As dialog or declined the wrong-project override, so the VM can always clear busy.</para>
    /// </summary>
    void ReconcileWorkbook(Action<ReconcileResult> onDone, Action<string> onError, Action onCancelled);
}
