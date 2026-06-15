#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Schedule.Models;

namespace TurboSuite.Schedule.Services;

/// <summary>
/// Shim-side <see cref="IScheduleWriter"/> — applies every dirty page in one transaction (one undo
/// entry). Symbols are re-resolved by Type Mark + category at save time. One unparseable value must
/// not abort the batch: <c>SetValueString</c> failures are collected and the field left dirty while
/// the rest commits. Invoked on the Revit API thread via the work queue.
/// </summary>
public class ScheduleWriterService : IScheduleWriter
{
    private readonly Document _doc;

    public ScheduleWriterService(Document doc)
    {
        _doc = doc;
    }

    public ScheduleWriteResult Write(IReadOnlyList<SpecWriteRequest> pages)
    {
        var result = new ScheduleWriteResult();

        using (var tx = new Transaction(_doc, "TurboSchedule - Save fixture specs"))
        {
            tx.Start();

            foreach (var page in pages)
            {
                var cat = page.Kind == PageKind.Fixture
                    ? BuiltInCategory.OST_LightingFixtures
                    : BuiltInCategory.OST_LightingDevices;

                var groups = ScheduleTypeCollector.SymbolsByTypeMark(_doc, cat);
                if (!groups.TryGetValue(page.TypeMark, out var symbols) || symbols.Count == 0)
                    continue;

                bool typeTouched = false;

                foreach (var fw in page.Fields)
                {
                    bool fieldOk = true;
                    bool wroteAny = false;

                    foreach (var symbol in symbols)
                    {
                        var p = ScheduleTypeCollector.Resolve(symbol, fw.ParamKey, fw.IsBuiltIn);
                        if (p == null || p.IsReadOnly) continue;

                        bool ok;
                        if (p.StorageType == StorageType.String)
                        {
                            ok = p.Set(fw.Value ?? "");
                        }
                        else if (string.IsNullOrWhiteSpace(fw.Value))
                        {
                            continue; // don't SetValueString("") — leave field dirty
                        }
                        else if (p.StorageType == StorageType.Integer && int.TryParse(fw.Value, out var iv))
                        {
                            ok = p.Set(iv); // Yes/No ("1"/"0") and plain integers — no unit parsing
                        }
                        else
                        {
                            ok = p.SetValueString(fw.Value); // parses display + units
                        }

                        wroteAny = true;
                        if (!ok) fieldOk = false;
                    }

                    if (wroteAny && fieldOk)
                    {
                        result.SavedKeys.Add(ScheduleWriteKey.For(page.TypeMark, page.Kind, fw.ParamKey));
                        typeTouched = true;
                    }
                    else
                    {
                        result.Skipped.Add($"{fw.Label} on {page.TypeMark}");
                    }
                }

                if (typeTouched) result.UpdatedTypes++;
            }

            tx.Commit();
        }

        return result;
    }
}
