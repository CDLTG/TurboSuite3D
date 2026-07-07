#nullable enable
using TurboSuite.Dmx.Input;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Revit-free contract for reading the active document into a <see cref="DmxModelSnapshot"/> — DMX
    /// fixtures (Channels + Control Zone + length + W/ft) and the discovered decoder/driver candidate
    /// pools. Implemented shim-side (binding to the active <c>Document</c>); the Core ViewModel invokes it
    /// through the <see cref="TurboSuite.Abstractions.IRevitWorkQueue"/> so the read runs on the Revit API
    /// thread. Read-only — never writes the model.
    /// </summary>
    public interface IDmxModelReader
    {
        DmxModelSnapshot Read();
    }
}
