// netstandard2.0 lacks the types the compiler needs for records, init accessors and nullable
// flow attributes; declaring them internally lets the analyzer use the current language.
#pragma warning disable IDE0130 // The namespace must match the one the compiler looks for.
namespace System.Runtime.CompilerServices;
#pragma warning restore IDE0130

/// <summary>Marks init-only setters; the compiler requires this type to exist.</summary>
internal static class IsExternalInit;
