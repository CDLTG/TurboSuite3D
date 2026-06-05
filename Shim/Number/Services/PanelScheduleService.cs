#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;

namespace TurboSuite.Number.Services
{
    public class PanelScheduleService
    {
        public PanelScheduleView GetOrCreateScheduleView(Document doc, ElementId panelId)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(PanelScheduleView))
                .Cast<PanelScheduleView>()
                .FirstOrDefault(psv => psv.GetPanel() == panelId);

            if (existing != null)
                return existing;

            using (Transaction tx = new Transaction(doc, "TurboNumber - Create Panel Schedule"))
            {
                tx.Start();
                var view = PanelScheduleView.CreateInstanceView(doc, panelId);
                tx.Commit();
                return view;
            }
        }

        public List<SlotInfo> GetSlotLayout(PanelScheduleView psv, Document doc)
        {
            var slots = new List<SlotInfo>();
            var seenSlots = new HashSet<int>();
            var sectionData = psv.GetSectionData(SectionType.Body);
            int rows = sectionData.NumberOfRows;
            int cols = sectionData.NumberOfColumns;

            // First pass: discover unique slot numbers
            var slotNumbers = new List<int>();
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    int slotNumber;
                    try
                    {
                        slotNumber = psv.GetSlotNumberByCell(row, col);
                    }
                    catch
                    {
                        continue;
                    }

                    if (slotNumber >= 0 && seenSlots.Add(slotNumber))
                        slotNumbers.Add(slotNumber);
                }
            }

            // Second pass: use GetCellsBySlotNumber for canonical coordinates
            foreach (int slotNumber in slotNumbers.OrderBy(n => n))
            {
                IList<int> slotRows;
                IList<int> slotCols;
                try
                {
                    psv.GetCellsBySlotNumber(slotNumber, out slotRows, out slotCols);
                }
                catch
                {
                    continue;
                }

                if (slotRows == null || slotRows.Count == 0 || slotCols == null || slotCols.Count == 0)
                    continue;

                // Use Last row, First col as the canonical anchor (per Revit API convention)
                int anchorRow = slotRows.Last();
                int anchorCol = slotCols.First();

                ElementId circuitId;
                try
                {
                    circuitId = psv.GetCircuitIdByCell(anchorRow, anchorCol);
                }
                catch
                {
                    circuitId = ElementId.InvalidElementId;
                }

                // Check IsSpare/IsSpace first — they have valid CircuitIds but are not real circuits
                string slotType;
                if (psv.IsSpare(anchorRow, anchorCol))
                    slotType = "Spare";
                else if (psv.IsSpace(anchorRow, anchorCol))
                    slotType = "Space";
                else if (circuitId != ElementId.InvalidElementId)
                    slotType = "Circuit";
                else
                    slotType = "Empty";

                slots.Add(new SlotInfo
                {
                    Row = anchorRow,
                    Col = anchorCol,
                    SlotNumber = slotNumber,
                    CircuitId = circuitId,
                    SlotType = slotType
                });
            }

            return slots;
        }

        public bool MoveCircuit(Document doc, PanelScheduleView psv,
            int fromRow, int fromCol, int toRow, int toCol)
        {
            if (fromRow == toRow && fromCol == toCol) return false;

            if (!psv.CanMoveSlotTo(fromRow, fromCol, toRow, toCol))
            {
                TaskDialog.Show("TurboNumber", "Cannot move circuit to that slot.");
                return false;
            }

            using (Transaction tx = new Transaction(doc, "TurboNumber - Move Circuit"))
            {
                tx.Start();
                try
                {
                    psv.MoveSlotTo(fromRow, fromCol, toRow, toCol);
                    tx.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("TurboNumber", $"Move failed: {ex.Message}");
                    return false;
                }
            }
        }
        public bool AssignSpare(Document doc, PanelScheduleView psv, int row, int col)
        {
            using (Transaction tx = new Transaction(doc, "TurboNumber - Assign Spare"))
            {
                tx.Start();
                try
                {
                    psv.AddSpare(row, col);
                    tx.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("TurboNumber", $"Assign Spare failed: {ex.Message}");
                    return false;
                }
            }
        }

        public bool AssignSpace(Document doc, PanelScheduleView psv, int row, int col)
        {
            using (Transaction tx = new Transaction(doc, "TurboNumber - Assign Space"))
            {
                tx.Start();
                try
                {
                    psv.AddSpace(row, col);
                    tx.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("TurboNumber", $"Assign Space failed: {ex.Message}");
                    return false;
                }
            }
        }

        public bool RemoveSpare(Document doc, PanelScheduleView psv, int row, int col)
        {
            using (Transaction tx = new Transaction(doc, "TurboNumber - Remove Spare"))
            {
                tx.Start();
                try
                {
                    psv.RemoveSpare(row, col);
                    tx.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("TurboNumber", $"Remove Spare failed: {ex.Message}");
                    return false;
                }
            }
        }

        public bool RemoveSpace(Document doc, PanelScheduleView psv, int row, int col)
        {
            using (Transaction tx = new Transaction(doc, "TurboNumber - Remove Space"))
            {
                tx.Start();
                try
                {
                    psv.RemoveSpace(row, col);
                    tx.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("TurboNumber", $"Remove Space failed: {ex.Message}");
                    return false;
                }
            }
        }

        public bool AssignSpareMultiple(Document doc, PanelScheduleView psv, List<(int Row, int Col)> slots)
        {
            using (Transaction tx = new Transaction(doc, "TurboNumber - Assign Spare"))
            {
                tx.Start();
                try
                {
                    foreach (var (row, col) in slots)
                        psv.AddSpare(row, col);
                    tx.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("TurboNumber", $"Assign Spare failed: {ex.Message}");
                    return false;
                }
            }
        }

        public bool AssignSpaceMultiple(Document doc, PanelScheduleView psv, List<(int Row, int Col)> slots)
        {
            using (Transaction tx = new Transaction(doc, "TurboNumber - Assign Space"))
            {
                tx.Start();
                try
                {
                    foreach (var (row, col) in slots)
                        psv.AddSpace(row, col);
                    tx.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("TurboNumber", $"Assign Space failed: {ex.Message}");
                    return false;
                }
            }
        }

        public bool RemoveSpareSpaceMultiple(Document doc, PanelScheduleView psv, List<(int Row, int Col, string SlotType)> slots)
        {
            using (Transaction tx = new Transaction(doc, "TurboNumber - Remove Spare/Space"))
            {
                tx.Start();
                try
                {
                    foreach (var (row, col, slotType) in slots)
                    {
                        if (slotType == "Spare")
                            psv.RemoveSpare(row, col);
                        else
                            psv.RemoveSpace(row, col);
                    }
                    tx.Commit();
                    return true;
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    TaskDialog.Show("TurboNumber", $"Remove failed: {ex.Message}");
                    return false;
                }
            }
        }
    }

    public class SlotInfo
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public int SlotNumber { get; set; }
        public ElementId CircuitId { get; set; }
        public string SlotType { get; set; } // "Circuit", "Empty", "Spare", "Space"
    }
}
