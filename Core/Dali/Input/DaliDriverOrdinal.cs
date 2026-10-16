#nullable enable

namespace TurboSuite.Dali.Input
{
    /// <summary>
    /// Reads a driver device's <b>within-circuit ordinal</b> from its Switch ID suffix — the down-column
    /// index TurboDriver stamps at deploy time (<c>DeploymentExecutor</c>: <c>baseSwitchId + (char)('a' + i)</c>
    /// when a circuit places more than one driver). A single-driver circuit gets no suffix (bare base, often
    /// the placeholder <c>"—"</c>) ⇒ ordinal 0.
    ///
    /// <para>Only the <b>suffix</b> is meaningful for identity/ordering: in real CDLTG models the base is a
    /// non-unique placeholder, so the durable key is <c>circuit.UniqueId + ordinal</c>, never the Switch ID
    /// string (see the plan's durable-key amendment). The strip rule mirrors
    /// <c>DeploymentExecutor.StripSwitchIdSuffix</c> exactly: a trailing lowercase letter counts as the suffix
    /// only when the char before it is not itself a lowercase letter (so an all-alphabetic id isn't misread).</para>
    /// </summary>
    public static class DaliDriverOrdinal
    {
        public static int FromSwitchId(string? switchId)
        {
            if (string.IsNullOrEmpty(switchId) || switchId!.Length < 2) return 0;

            char last = switchId[switchId.Length - 1];
            char secondLast = switchId[switchId.Length - 2];

            if (last >= 'a' && last <= 'z' && !(secondLast >= 'a' && secondLast <= 'z'))
                return last - 'a';

            return 0;
        }

        /// <summary>The Switch ID with its ordinal suffix stripped — the <b>base</b> for a row label. There is
        /// no existing public string-stripper (<c>DeploymentExecutor.StripSwitchIdSuffix</c> is private), so this
        /// is the Core twin, applying the identical rule to <see cref="FromSwitchId"/>: a trailing lowercase
        /// letter is the suffix only when the char before it is not itself lowercase. Returns the input verbatim
        /// when there is no strippable suffix.</summary>
        public static string BaseOf(string? switchId)
        {
            if (string.IsNullOrEmpty(switchId) || switchId!.Length < 2) return switchId ?? "";

            char last = switchId[switchId.Length - 1];
            char secondLast = switchId[switchId.Length - 2];

            if (last >= 'a' && last <= 'z' && !(secondLast >= 'a' && secondLast <= 'z'))
                return switchId.Substring(0, switchId.Length - 1);

            return switchId;
        }
    }
}
