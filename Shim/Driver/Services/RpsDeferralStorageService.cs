#nullable disable
using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Persists the TurboRPS "deferred" flag in ExtensibleStorage attached directly to each circuit
    /// (<c>ElectricalSystem</c>) element — so it travels with the model (every user sees the same
    /// deferrals) and auto-clears if the circuit is deleted/rewired. Unlike the document-singleton
    /// settings services, this stores a per-element entity.
    ///
    /// Schema GUID is versioned — change it on any field add/remove (see CLAUDE.md "ExtensibleStorage
    /// Schema Changes").
    /// </summary>
    public static class RpsDeferralStorageService
    {
        private static readonly Guid SchemaGuid = new("d4e5f6a7-b8c9-0123-defa-3456789012ab");
        private const string SchemaName = "TurboSuiteRpsDeferral";
        private const string DeferredField = "Deferred";
        private const string SignatureField = "ConfigSignature";

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(DeferredField, typeof(bool));
            builder.AddSimpleField(SignatureField, typeof(string));
            return builder.Finish();
        }

        /// <summary>Reads the deferral state off a circuit element. Returns (false, null) when the
        /// schema/entity is absent or the element is null.</summary>
        public static (bool Deferred, string Signature) Read(Element circuit)
        {
            if (circuit == null) return (false, null);

            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return (false, null);

            var entity = circuit.GetEntity(schema);
            if (!entity.IsValid()) return (false, null);

            bool deferred = schema.GetField(DeferredField) != null && entity.Get<bool>(DeferredField);
            string signature = schema.GetField(SignatureField) != null ? entity.Get<string>(SignatureField) : null;
            return (deferred, signature);
        }

        /// <summary>Writes the deferral state to a circuit element inside its own transaction.
        /// Clearing stores Deferred=false with an empty signature.</summary>
        public static void Save(Document doc, Element circuit, bool deferred, string signature)
        {
            var schema = GetOrCreateSchema();

            using var tx = new Transaction(doc, "TurboRPS — Defer circuit");
            tx.Start();

            var entity = new Entity(schema);
            entity.Set(DeferredField, deferred);
            // ExtensibleStorage string fields cannot be null.
            entity.Set(SignatureField, signature ?? string.Empty);
            circuit.SetEntity(entity);

            tx.Commit();
        }
    }
}
