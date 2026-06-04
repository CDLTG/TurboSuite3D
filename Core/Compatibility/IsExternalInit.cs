// Compile-time polyfill for C# records / init-only setters.
//
// The compiler emits a reference to System.Runtime.CompilerServices.IsExternalInit
// for every `init` accessor (and positional record). That type ships in .NET 5+
// (so the net8.0-windows main project has it), but it does NOT exist in
// netstandard2.0 OR .NET Framework 4.8 — so both TurboSuite.Core and the future
// net48 Revit2024 shim need this stub to compile any record / init member.
//
// It is purely a compile-time marker (consumed as a modreq on the setter); the
// runtime never instantiates it, and the .NET 5+ Revit host supplying the real
// type alongside this internal stub is harmless.
//
// Guarded so it drops out on net8 (where the real type exists) once Core
// multi-targets net48;net8.0-windows.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
