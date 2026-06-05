using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace TurboSuite.Docs.Services;

public static class NotesCollectorService
{
    private const string GeneralScheduleName = "Notes_General";
    private const string ControlScheduleName = "Notes_Controls";

    public static List<string> CollectGeneralNotes(Document doc)
    {
        return CollectFromKeySchedule(doc, GeneralScheduleName);
    }

    public static List<string> CollectControlNotes(Document doc)
    {
        return CollectFromKeySchedule(doc, ControlScheduleName);
    }

    private static List<string> CollectFromKeySchedule(Document doc, string scheduleName)
    {
        var schedule = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .FirstOrDefault(vs => vs.Name == scheduleName);

        if (schedule == null) return new List<string>();

        var tableData = schedule.GetTableData();
        var bodySection = tableData.GetSectionData(SectionType.Body);
        int rowCount = bodySection.NumberOfRows;
        int colCount = bodySection.NumberOfColumns;

        // Find the "Comments" column index
        int commentsCol = -1;
        var headerSection = tableData.GetSectionData(SectionType.Header);
        if (headerSection.NumberOfRows > 0)
        {
            for (int c = 0; c < colCount; c++)
            {
                string header = schedule.GetCellText(SectionType.Header, headerSection.NumberOfRows - 1, c);
                if (header == "Comments")
                {
                    commentsCol = c;
                    break;
                }
            }
        }

        // Fallback: if only two columns, Comments is likely the second
        if (commentsCol < 0 && colCount == 2)
            commentsCol = 1;

        if (commentsCol < 0) return new List<string>();

        var notes = new List<string>();
        for (int r = 0; r < rowCount; r++)
        {
            string text = schedule.GetCellText(SectionType.Body, r, commentsCol);
            if (!string.IsNullOrWhiteSpace(text))
                notes.Add(text.Trim());
        }

        return notes;
    }
}
