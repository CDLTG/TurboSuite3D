// Make the netstandard2.0/net48 string polyfills ambient across all of Core so
// migrated logic can use modern overloads (Contains/Replace with StringComparison,
// Split(char, int)) without per-file using directives. Independent of the
// ImplicitUsings MSBuild switch (a C# 10 global-using directive).
global using TurboSuite.Compatibility;

// Keep the TurboSuite.Compatibility namespace in existence on net8, where every
// polyfill above is #if-guarded out. Without this, the namespace would have zero
// members on the net8 target and the global using above would fail with CS0246.
// On net48 the real polyfill types populate the namespace; this empty declaration
// is harmless there.
namespace TurboSuite.Compatibility { }
