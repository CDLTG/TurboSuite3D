#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using TurboSuite.Shared.Storage;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// The storage-generation guard. A project carries a <b>stamp</b> recording the highest
    /// <see cref="StorageGeneration"/> that has saved TurboSuite data into it; a build of a lower generation
    /// refuses its ExtensibleStorage saves there, so a schema change can't split the project's data between an
    /// old store and a new one. Model edits are never blocked — only stored data.
    ///
    /// <para><b>The stamp schema is permanent.</b> Never change its GUID, name or fields: TurboSpike T4b showed
    /// that a changed field set under an existing GUID makes Revit's "Data Conflict - Add-Ons" dialog delete
    /// the file's data for that schema on open.</para>
    ///
    /// <para><b>Append-only.</b> Raising the generation creates a <i>new</i> stamp <see cref="DataStorage"/>;
    /// existing stamps are never edited. A new element needs no worksharing borrow (editing one a teammate
    /// created would, and no lease names it), and duplicate stamps from two users stamping before either syncs
    /// are harmless because the project's generation is the <b>max</b> over all of them — never the
    /// lowest-ElementId pick (TurboSpike T5: sync renumbering makes id order meaningless).</para>
    ///
    /// <para>Exempt: <see cref="ModuleLockStorageService"/> (worksharing locks, not data) and this stamp.
    /// A stamp is only visible to teammates once the stamping user syncs and they reload. See
    /// <c>Shim/Shared/Services/WORKSHARING.md</c>.</para>
    /// </summary>
    public static class StorageGenerationService
    {
        private static readonly Guid SchemaGuid = new("9ef91f83-68e1-47ba-9f29-2a05e34b0abe");
        private const string SchemaName = "TurboSuiteStorageGeneration";
        private const string GenerationField = "Generation";
        private const string WrittenByField = "WrittenBy";

        // Per-session, per-document UI memory (keyed by DataStorageHelper.DocKey).
        private static readonly HashSet<string> _warnedBlocked = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> _devStampDecision = new(StringComparer.OrdinalIgnoreCase);

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(GenerationField, typeof(int));
            builder.AddSimpleField(WrittenByField, typeof(string));
            return builder.Finish();
        }

        /// <summary>The project's storage generation (max over every stamp; 0 when unstamped) and who wrote
        /// the highest stamp.</summary>
        public static (int Generation, string WrittenBy) Read(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return (StorageGenerationGate.Unstamped, null);

            var stamps = new List<(int Gen, string By)>();
            using (var collector = new FilteredElementCollector(doc))
            {
                foreach (var ds in collector.OfClass(typeof(DataStorage)).Cast<DataStorage>())
                {
                    var e = ds.GetEntity(schema);
                    if (!e.IsValid()) continue;
                    stamps.Add((e.Get<int>(GenerationField), e.Get<string>(WrittenByField)));
                }
            }

            int gen = StorageGenerationGate.DocGeneration(stamps.Select(s => s.Gen));
            string by = stamps.Where(s => s.Gen == gen).Select(s => s.By).FirstOrDefault();
            return (gen, by);
        }

        /// <summary>
        /// Call <b>inside the save's open transaction</b>, before writing: may this ExtensibleStorage save
        /// proceed? Stamps the project when this build is newer than it (never in a template). On refusal the
        /// user is told once per document per <b>store</b> per session — so each tool explains its own first
        /// refused save, and an auto-saving window still shows it only once. Callers stay silent and report
        /// <c>Blocked</c>.
        /// </summary>
        /// <param name="storeLabel">What the user would call this data ("TurboZones panel settings").</param>
        public static bool AuthorizeWrite(Document doc, string storeLabel) =>
            AuthorizeWrite(doc, storeLabel, StorageGeneration.Current);

        /// <summary><see cref="AuthorizeWrite(Document, string)"/> as a build of <paramref name="buildGeneration"/> —
        /// the seam TurboSpike uses to act as a newer build. Production code calls the two-argument form.</summary>
        internal static bool AuthorizeWrite(Document doc, string storeLabel, int buildGeneration)
        {
            var (docGen, writtenBy) = Read(doc);

            if (!StorageGenerationGate.CanWrite(docGen, buildGeneration))
            {
                WarnBlockedOnce(doc, storeLabel, docGen, writtenBy, buildGeneration);
                return false;
            }

            bool isTemplate = StorageGenerationGate.IsTemplatePath(DataStorageHelper.DocPath(doc));
            if (!StorageGenerationGate.ShouldStamp(docGen, buildGeneration, isTemplate)) return true;

#if DEBUG
            if (!ConfirmDevStamp(doc, docGen, buildGeneration)) return false;
#endif
            Stamp(doc, buildGeneration);
            return true;
        }

        private static void Stamp(Document doc, int generation)
        {
            var schema = GetOrCreateSchema();
            var entity = new Entity(schema);
            entity.Set(GenerationField, generation);
            entity.Set(WrittenByField, BuildLabel(generation));
            DataStorage.Create(doc).SetEntity(entity); // append-only: never edit an existing stamp
        }

        private static string BuildLabel(int generation)
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
#if DEBUG
            const string flavor = " dev";
#else
            const string flavor = "";
#endif
            return $"TurboSuite v{v.ToString(3)}{flavor} (storage generation {generation})";
        }

        private static void WarnBlockedOnce(Document doc, string storeLabel, int docGen, string writtenBy, int buildGen)
        {
            string what = string.IsNullOrWhiteSpace(storeLabel) ? "TurboSuite settings" : storeLabel;
            if (!_warnedBlocked.Add($"{DataStorageHelper.DocKey(doc)}|{what}")) return;
            string by = string.IsNullOrWhiteSpace(writtenBy) ? $"a newer TurboSuite (storage generation {docGen})" : writtenBy;
            TryShow(
                $"Your change to {what} wasn't saved.\n\n" +
                $"This project's TurboSuite data was saved by {by}, newer than this build (storage generation " +
                $"{buildGen}). Saving from this build would split the project's data between two versions, so it " +
                "saves no TurboSuite settings here.\n\n" +
                "Update TurboSuite and restart Revit to save settings in this project. Model edits are not affected.");
        }

#if DEBUG
        /// <summary>Dev builds run ahead of the release; raising a real project's generation blocks every
        /// teammate's TurboSuite saves there until they update. Ask once per document per session.</summary>
        private static bool ConfirmDevStamp(Document doc, int docGen, int buildGen)
        {
            string key = DataStorageHelper.DocKey(doc);
            if (_devStampDecision.TryGetValue(key, out bool decided)) return decided;

            bool ok;
            try
            {
                var td = new TaskDialog("Dev build")
                {
                    MainInstruction = $"Raise this project's TurboSuite storage generation to {buildGen}?",
                    MainContent =
                        $"\"{doc.Title}\" is at generation {docGen}. Once stamped, released builds (generation " +
                        $"{buildGen - 1} and below) can't save TurboSuite settings in it until they update.\n\n" +
                        "Continue only on a test copy. No = TurboSuite won't save settings in this project for the " +
                        "rest of this Revit session.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No
                };
                ok = td.Show() == TaskDialogResult.Yes;
            }
            catch { ok = false; }

            _devStampDecision[key] = ok;
            return ok;
        }
#endif

        private static void TryShow(string text)
        {
            try { TaskDialog.Show("TurboSuite", text); }
            catch { /* a notice must never break the save path it rides on */ }
        }
    }

    /// <summary>
    /// One-time notice for a JSON store whose saved payload this build couldn't read (plan §2e): the save is
    /// refused so the unreadable data is never overwritten with an empty state.
    /// </summary>
    internal static class UnreadablePayloadNotice
    {
        private static readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

        public static void ShowOnce(Document doc, string storeLabel)
        {
            if (!_warned.Add($"{DataStorageHelper.DocKey(doc)}|{storeLabel}")) return;
            try
            {
                TaskDialog.Show("TurboSuite",
                    $"Your change to {storeLabel} wasn't saved.\n\n" +
                    $"TurboSuite couldn't read this project's saved {storeLabel} — it may have been written by a " +
                    "newer TurboSuite. To protect it, this build won't overwrite it.\n\n" +
                    "Update TurboSuite and restart Revit to save these settings here.");
            }
            catch { /* never break the save path */ }
        }
    }
}
