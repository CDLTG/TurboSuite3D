namespace TurboSuite.Schedule.Models;

/// <summary>
/// Which native schedule a page's Type Mark group came from. Fixtures
/// (<c>OST_LightingFixtures</c>) and Drivers (<c>OST_LightingDevices</c>) share most of the
/// form but diverge in the Electrical/Mechanical/Photometric sections — see <see cref="FieldDef"/>.
/// </summary>
public enum PageKind
{
    Fixture,
    Driver
}
