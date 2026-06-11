#nullable disable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.IO;
using Autodesk.Revit.DB;

namespace TurboSuite.Name.Services;

/// <summary>
/// Static helpers for locating and loading the DWG behind a linked <see cref="ImportInstance"/>.
/// Used by the in-app CAD Room Source discovery (Settings dialog). Intentionally does NOT refactor
/// <see cref="CadRoomExtractorService"/> — the shipping extractor is left untouched to avoid
/// regression; the small switch duplication here is acceptable.
/// </summary>
public static class CadLinkResolver
{
    /// <summary>Linked CAD imports (<see cref="ImportInstance.IsLinked"/>) visible in the given view.</summary>
    public static List<ImportInstance> GetLinkedImports(Document doc, View view)
    {
        return new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(ImportInstance))
            .Cast<ImportInstance>()
            .Where(ii => ii.IsLinked)
            .ToList();
    }

    /// <summary>
    /// Resolves the on-disk DWG path for a linked import. Returns false if the type isn't a
    /// <see cref="CADLinkType"/>, the external reference is missing, or the file doesn't exist.
    /// </summary>
    public static bool TryGetDwgPath(Document doc, ImportInstance import, out string path)
    {
        path = null;
        if (doc.GetElement(import.GetTypeId()) is not CADLinkType cadLinkType) return false;

        var extRef = cadLinkType.GetExternalFileReference();
        if (extRef == null || extRef.GetAbsolutePath() == null) return false;

        path = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
        return File.Exists(path);
    }

    /// <summary>
    /// Reads a DWG into a <see cref="CadDocument"/>, re-throwing a friendly message if the file is
    /// locked open in another application (mirrors <see cref="CadRoomExtractorService"/>).
    /// </summary>
    public static CadDocument Load(string path)
    {
        try
        {
            using var reader = new DwgReader(path);
            return reader.Read();
        }
        catch (IOException)
        {
            string fileName = Path.GetFileName(path);
            throw new IOException(
                $"Cannot read \"{fileName}\" because it is open in another application.\n\n" +
                "Close the file in AutoCAD and try again.");
        }
    }

    /// <summary>DWG-units-to-feet factor (same mapping as the production extractor).</summary>
    public static double GetUnitToFeetFactor(ACadSharp.Types.Units.UnitsType units)
    {
        return units switch
        {
            ACadSharp.Types.Units.UnitsType.Inches => 1.0 / 12.0,
            ACadSharp.Types.Units.UnitsType.Feet => 1.0,
            ACadSharp.Types.Units.UnitsType.Millimeters => 1.0 / 304.8,
            ACadSharp.Types.Units.UnitsType.Centimeters => 1.0 / 30.48,
            ACadSharp.Types.Units.UnitsType.Meters => 1.0 / 0.3048,
            _ => 1.0 / 12.0, // default to inches
        };
    }
}
