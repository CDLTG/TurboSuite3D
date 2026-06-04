// Make the netstandard2.0/net48 string polyfills ambient across all of Core so
// migrated logic can use modern overloads (Contains/Replace with StringComparison,
// Split(char, int)) without per-file using directives. Independent of the
// ImplicitUsings MSBuild switch (a C# 10 global-using directive).
global using TurboSuite.Compatibility;
