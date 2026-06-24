#nullable disable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Schedule.Models;

/// <summary>A catalog #/qty pair rendered inline on one row.</summary>
public class CatalogRow
{
    public SpecField Number { get; set; }
    public SpecField Qty { get; set; }
}

/// <summary>
/// One editable page: a Type Mark group and the <see cref="SpecField"/>s applicable to its
/// <see cref="Kind"/>, pre-sectioned for the form. Built by the shim collector.
/// </summary>
public class FixtureTypeSpec : ViewModelBase
{
    public FixtureTypeSpec(string typeMark, PageKind kind, IReadOnlyList<SpecField> fields)
    {
        TypeMark = typeMark;
        Kind = kind;
        AllFields = fields;

        foreach (var f in fields)
            f.DirtyChanged += _ => RaiseDirty();

        IdentityFields = fields
            .Where(f => f.Section == SpecSection.Identity && f.Role == FieldRole.Normal)
            .ToList();

        CatalogRows = fields
            .Where(f => f.Role == FieldRole.CatalogNumber)
            .OrderBy(f => f.Def.Slot)
            .Select(num => new CatalogRow
            {
                Number = num,
                Qty = fields.FirstOrDefault(q => q.Role == FieldRole.CatalogQty && q.Def.Slot == num.Def.Slot)
            })
            .ToList();

        ElectricalFields = fields.Where(f => f.Section == SpecSection.Electrical).ToList();
        MechanicalFields = fields.Where(f => f.Section == SpecSection.Mechanical).ToList();
        PhotometricFields = fields.Where(f => f.Section == SpecSection.Photometric).ToList();
        NoteFields = fields.Where(f => f.Section == SpecSection.Notes).OrderBy(f => f.Def.Slot).ToList();
    }

    public string TypeMark { get; }
    public PageKind Kind { get; }
    public IReadOnlyList<SpecField> AllFields { get; }

    public List<SpecField> IdentityFields { get; }
    public List<CatalogRow> CatalogRows { get; }
    public List<SpecField> ElectricalFields { get; }
    public List<SpecField> MechanicalFields { get; }
    public List<SpecField> PhotometricFields { get; }
    public List<SpecField> NoteFields { get; }

    public bool IsFixture => Kind == PageKind.Fixture;
    public bool HasPhotometric => Kind == PageKind.Fixture && PhotometricFields.Count > 0;

    public bool IsDirty => AllFields.Any(f => f.IsDirty);

    public IEnumerable<SpecField> SectionFields(SpecSection section) =>
        AllFields.Where(f => f.Section == section);

    private void RaiseDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        DirtyChanged?.Invoke(this);
    }

    public event System.Action<FixtureTypeSpec> DirtyChanged;
}
