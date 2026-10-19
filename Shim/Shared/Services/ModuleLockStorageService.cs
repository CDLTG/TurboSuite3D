#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// One tiny <see cref="DataStorage"/> per modeless module, used purely as a worksharing lock: a module's
    /// session lease (<see cref="WorksharingLease.AcquireSession"/>) borrows its lock store, so while one user
    /// holds it (until they sync) no other TurboSuite user can open that module. The entity carries only the
    /// module name — nothing reads it as data.
    ///
    /// <para>Additive schema with its own GUID (bump on any field add/remove — see CLAUDE.md "ExtensibleStorage
    /// Schema Changes"). Only ever created on a workshared document.</para>
    /// </summary>
    public static class ModuleLockStorageService
    {
        private static readonly Guid SchemaGuid = new Guid("5c0b8d2e-7a41-4f6e-9b3d-2e8f1a6c4d70");
        private const string SchemaName = "TurboSuiteModuleLock";
        private const string ModuleField = "Module";

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(ModuleField, typeof(string));
            return builder.Finish();
        }

        /// <summary>
        /// Every lock store for <paramref name="module"/>, creating one (in its own committed transaction) if
        /// none exists. Returns <b>all</b> matches, not one: two users can each create the lock before either
        /// syncs, and leasing every copy keeps the lock meaningful instead of warning about the duplicate.
        /// A just-created store is owned by its creator, so creation never contends. Returns an empty list if
        /// the creation was rolled back. Must be called with no transaction open.
        /// </summary>
        public static List<ElementId> EnsureLockIds(Document doc, string module)
        {
            var schema = GetOrCreateSchema();
            var ids = FindLockIds(doc, schema, module);
            if (ids.Count > 0) return ids;

            ElementId created;
            using (var tx = new Transaction(doc, $"TurboSuite - Create {module} lock"))
            {
                tx.Start();
                var storage = DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(ModuleField, module);
                storage.SetEntity(entity);
                created = storage.Id;
                if (!CommitGuard.TryCommit(tx)) return new List<ElementId>();
            }
            return new List<ElementId> { created };
        }

        private static List<ElementId> FindLockIds(Document doc, Schema schema, string module)
        {
            using (var collector = new FilteredElementCollector(doc))
            {
                return collector
                    .OfClass(typeof(DataStorage))
                    .Cast<DataStorage>()
                    .Where(ds =>
                    {
                        var e = ds.GetEntity(schema);
                        return e.IsValid() && string.Equals(e.Get<string>(ModuleField), module, StringComparison.Ordinal);
                    })
                    .Select(ds => ds.Id)
                    .ToList();
            }
        }
    }
}
