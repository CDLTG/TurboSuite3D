using System.Collections.Generic;
using TurboSuite.Number.ViewModels;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// Revit-free contract for persisting per-device "Switch ID" values. Implemented
    /// shim-side (wraps <c>NumberWriterService.WriteDeviceSwitchIds</c>); the Core
    /// ViewModel calls it inside an <see cref="TurboSuite.Abstractions.IRevitWorkQueue"/>
    /// work item so the transaction runs on the Revit API thread.
    /// </summary>
    public interface ISwitchIdWriter
    {
        void WriteSwitchIds(IReadOnlyList<NumberableRowViewModel> rows);
    }

    /// <summary>
    /// Revit-free contract for persisting the power-supply numbering prefix/suffix to
    /// ExtensibleStorage. Implemented shim-side (wraps <c>RoomOrderStorageService</c>).
    /// The matching load is performed at collection time and passed into the ViewModel
    /// ctor, since a Core ctor cannot read Revit synchronously.
    /// </summary>
    public interface IPrefixSuffixStore
    {
        void Save(string prefix, string suffix);
    }

    /// <summary>
    /// Revit-free contract for persisting the project-wide room order (per-room click
    /// order) and the keypad tab's room-sort toggle to ExtensibleStorage. Implemented
    /// shim-side (room order → shared <c>RoomOrderStorageService</c>; toggle →
    /// TurboNumber-local <c>CircuitNamingStorageService</c>). The matching loads run at
    /// collection time and are passed into the ViewModel ctors.
    /// </summary>
    public interface IRoomOrderStore
    {
        void SaveRoomOrder(IReadOnlyList<(string Name, int ClickOrder)> roomOrder);
        void SaveKeypadRoomSorted(bool isSorted);
    }

    /// <summary>
    /// Revit-free contract for selecting and revealing a device in the active project.
    /// Implemented shim-side (wraps <c>UIDocument.Selection</c> + <c>ShowElements</c>);
    /// the Core ViewModel calls it inside an
    /// <see cref="TurboSuite.Abstractions.IRevitWorkQueue"/> work item so it runs on the
    /// Revit API thread. Returns false when the element no longer exists.
    /// </summary>
    public interface IDeviceSelector
    {
        bool SelectInProject(TurboSuite.Abstractions.ElementRef elementRef);
    }
}
