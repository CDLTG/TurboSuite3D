#nullable disable
using System.Collections.Generic;
using TurboSuite.Abstractions;

namespace TurboSuite.Driver.Models
{
    /// <summary>
    /// One user-created link: an RPS fixture <b>type</b> → a driver <b>family</b>. Created and removed
    /// only in TurboRPS → Driver Match and persisted per project (ExtensibleStorage). A link is a hard
    /// restriction on the matcher: a circuit carrying the fixture type may only be served by the linked
    /// family's types (TurboDriver still sizes within the family). Links break the tie two otherwise
    /// indistinguishable driver families would produce.
    /// </summary>
    public class DriverLink
    {
        /// <summary>Persisted key: the fixture type's UniqueId.</summary>
        public string FixtureTypeKey { get; set; }

        /// <summary>Persisted key: the driver family's UniqueId.</summary>
        public string DriverFamilyKey { get; set; }

        /// <summary>Resolved at load; <see cref="ElementRef.None"/> when the fixture type is gone.</summary>
        public ElementRef FixtureTypeRef { get; set; }

        /// <summary>Resolved at load; <see cref="ElementRef.None"/> when the driver family is gone.</summary>
        public ElementRef DriverFamilyRef { get; set; }

        /// <summary>"Family : Type" of the fixture (or a "(deleted fixture type)" note when broken).</summary>
        public string FixtureDisplay { get; set; }

        /// <summary>The driver family name (or a "(deleted driver family)" note when broken).</summary>
        public string FamilyDisplay { get; set; }

        /// <summary>True when either end no longer resolves. A broken link is ignored by the matcher and
        /// shown flagged in the links list so the user can remove it.</summary>
        public bool IsBroken { get; set; }
    }

    /// <summary>
    /// Immutable lookup the matcher consumes: fixture type → linked driver family, built from the
    /// non-broken <see cref="DriverLink"/>s only.
    /// </summary>
    public sealed class DriverLinks
    {
        private readonly Dictionary<ElementRef, ElementRef> _map;

        public static readonly DriverLinks Empty = new DriverLinks(new Dictionary<ElementRef, ElementRef>());

        private DriverLinks(Dictionary<ElementRef, ElementRef> map) => _map = map;

        /// <summary>Build from the loaded links, skipping broken or unresolved ones.</summary>
        public static DriverLinks From(IEnumerable<DriverLink> links)
        {
            var map = new Dictionary<ElementRef, ElementRef>();
            if (links != null)
            {
                foreach (var l in links)
                {
                    if (l == null || l.IsBroken || !l.FixtureTypeRef.IsValid || !l.DriverFamilyRef.IsValid)
                        continue;
                    map[l.FixtureTypeRef] = l.DriverFamilyRef;
                }
            }
            return map.Count == 0 ? Empty : new DriverLinks(map);
        }

        /// <summary>Build directly from a fixture-type → family map (tests, callers without keys).</summary>
        public static DriverLinks From(IDictionary<ElementRef, ElementRef> map)
            => map == null || map.Count == 0 ? Empty : new DriverLinks(new Dictionary<ElementRef, ElementRef>(map));

        public int Count => _map.Count;

        public bool TryGetFamily(ElementRef fixtureType, out ElementRef family)
        {
            if (fixtureType.IsValid && _map.TryGetValue(fixtureType, out family))
                return true;
            family = ElementRef.None;
            return false;
        }
    }
}
