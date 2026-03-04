#nullable disable
using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Service for updating elements in Revit
    /// </summary>
    public class ElementUpdateService
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;

        public ElementUpdateService(Document doc, UIDocument uidoc)
        {
            _doc = doc;
            _uidoc = uidoc;
        }

        /// <summary>
        /// Change the family type of a lighting device
        /// </summary>
        public bool ChangeFamilyType(ElementId deviceId, FamilySymbol newType)
        {
            if (deviceId == null || deviceId == ElementId.InvalidElementId)
                return false;

            if (newType == null)
                return false;

            using (Transaction trans = new Transaction(_doc, "Change Lighting Device Type"))
            {
                trans.Start();
                try
                {
                    FamilyInstance device = _doc.GetElement(deviceId) as FamilyInstance;
                    if (device == null)
                    {
                        trans.RollBack();
                        return false;
                    }

                    if (!newType.IsActive)
                    {
                        newType.Activate();
                        _doc.Regenerate();
                    }

                    device.Symbol = newType;

                    trans.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    TaskDialog.Show("Error Changing Type",
                        $"Failed to change device type:\n{ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Get the current family type name for a device
        /// </summary>
        public string GetCurrentTypeName(ElementId deviceId)
        {
            if (deviceId == null || deviceId == ElementId.InvalidElementId)
                return string.Empty;

            FamilyInstance device = _doc.GetElement(deviceId) as FamilyInstance;
            return device?.Symbol?.Name ?? string.Empty;
        }
    }
}
