using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using NPlusOne.EntityFrameworkCore.Recording;

namespace NPlusOne.Benchmarks.Runtime;

/// <summary>
/// The call site of one command or lazy load, captured on every record while <c>CaptureCallSite</c> is on (the default).
/// The stack depth matters: a request in ASP.NET Core runs some sixty frames below the application's code.
/// </summary>
public class CallSiteBenchmarks
{
    /// <summary>How many application frames sit above the capture.</summary>
    [Params(10, 60)]
    public int Depth { get; set; }

    /// <summary>One capture, <see cref="Depth"/> frames down.</summary>
    /// <returns>The call site.</returns>
    [Benchmark]
    public string? Capture() => Descend(Depth);

    /// <summary>Recurses to the requested depth, then captures; not inlined, so every level is a frame.</summary>
    /// <param name="depth">Frames still to add.</param>
    /// <returns>The call site.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? Descend(int depth) => depth == 0 ? CallSiteLocator.Capture(null) : Descend(depth - 1);
}
