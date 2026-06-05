#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Number.ViewModels;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// Shim-side <see cref="ISwitchIdWriter"/> — binds the Revit-free contract to the
    /// active document and the existing <see cref="NumberWriterService"/>. Must be
    /// invoked on the Revit API thread (via <see cref="RevitWorkQueue"/>).
    /// </summary>
    public class SwitchIdWriter : ISwitchIdWriter
    {
        private readonly Document _doc;
        private readonly NumberWriterService _writer;

        public SwitchIdWriter(Document doc, NumberWriterService writer)
        {
            _doc = doc;
            _writer = writer;
        }

        public void WriteSwitchIds(IReadOnlyList<NumberableRowViewModel> rows)
            => _writer.WriteDeviceSwitchIds(_doc, (IList<NumberableRowViewModel>)rows);
    }
}
