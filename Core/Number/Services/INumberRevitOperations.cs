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
}
