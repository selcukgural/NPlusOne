using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NPlusOne.EntityFrameworkCore.Recording;

/// <summary>
/// Finds and formats the code that issued a command or a lazy load: the first stack frame whose type
/// is not part of EF Core, a database provider, the .NET libraries, Castle proxies or the guard.
/// </summary>
internal static class CallSiteLocator
{
    /// <summary>
    /// Namespace roots that belong to frameworks, never to the application: .NET, EF Core and its
    /// providers (Npgsql, Pomelo, Oracle), proxies, and EF Core extension libraries named <c>EFCore.*</c>.
    /// Matched as whole segments, so <c>SystemsInc.Orders</c> is still application code.
    /// </summary>
    private static readonly string[] FrameworkNamespaces = ["System", "Microsoft", "Castle", "Npgsql", "Pomelo", "Oracle", "MySqlConnector", "EFCore"];

    /// <summary>
    /// The formatted call site of each code location seen so far: per method, by IL offset. Reading a frame's file and
    /// line walks the stack again and reads the PDB, which costs more than the rest of the capture together, and a code
    /// location always has the same file and line. Keyed weakly by the method, so a collectible assembly can still be
    /// unloaded; the entries are bounded by the code locations that send commands.
    /// </summary>
    private static readonly ConditionalWeakTable<MethodBase, ConcurrentDictionary<int, string>> Sites = [];

    /// <summary>This assembly; its frames are never the call site.</summary>
    private static readonly Assembly GuardAssembly = typeof(CallSiteLocator).Assembly;

    /// <summary>
    /// The namespace of the ASP.NET Core integration, whose middleware and filters sit below the
    /// application on the stack. Compared exactly, so an application namespace that starts with it
    /// (such as its test project) still counts as application code.
    /// </summary>
    private const string AspNetCoreNamespace = "NPlusOne.AspNetCore";

    /// <summary>
    /// Walks the current stack and formats the first application frame as
    /// <c>Type.Method (File.cs:line)</c>, or <c>Type.Method</c> when no symbols are available.
    /// </summary>
    /// <param name="entityType">
    /// For lazy loads, the entity type: its property getters (which trigger the load when the entity
    /// uses an injected <c>ILazyLoader</c>) are skipped so the caller of the getter is reported.
    /// </param>
    /// <returns>The call site, or <see langword="null"/> when no application frame is on the stack.</returns>
    public static string? Capture(Type? entityType)
    {
        // Walking without file information is cheap; symbols are then read for the one frame we keep, once per location.
        var frames = new StackTrace(1, false).GetFrames();
        for (var index = 0; index < frames.Length; index++)
        {
            var frame = frames[index];
            var method = frame.GetMethod();
            if (method?.DeclaringType is not { } type || IsFramework(type) || IsEntityAccessor(method, type, entityType))
            {
                continue;
            }

            var offset = frame.GetILOffset();
            var lines = offset == StackFrame.OFFSET_UNKNOWN ? null : Sites.GetValue(method, static _ => new ConcurrentDictionary<int, string>());
            if (lines is not null && lines.TryGetValue(offset, out var known))
            {
                return known;
            }

            // StackTrace(1) skipped this method; StackFrame(index + 1) counts it, so both point at the same frame. When
            // they do not, the site is formatted without a line and not kept, so a later capture can still find it.
            var located = new StackFrame(index + 1, true);
            if (located.GetMethod() != method)
            {
                return Format(type, method, null, 0);
            }

            var site = Format(type, method, located.GetFileName(), located.GetFileLineNumber());
            return lines is null ? site : lines.GetOrAdd(offset, site);
        }

        return null;
    }

    /// <summary>Formats a frame, unwrapping async state machines, lambdas and local functions to the method that declared them.</summary>
    /// <param name="type">The declaring type of the frame's method.</param>
    /// <param name="method">The frame's method.</param>
    /// <param name="file">The source file, when symbols are available.</param>
    /// <param name="line">The source line, or 0.</param>
    /// <returns>The formatted call site.</returns>
    internal static string Format(Type type, MethodBase method, string? file, int line)
    {
        var methodName = method.Name;
        var owner = type;
        if (methodName == "MoveNext" && IsCompilerGenerated(owner))
        {
            methodName = owner.Name;
        }

        while (IsCompilerGenerated(owner) && owner.DeclaringType is { } outer)
        {
            owner = outer;
        }

        var text = $"{StripArity(owner.Name)}.{UnwrapGeneratedName(methodName)}";
        return file is null || line <= 0 ? text : $"{text} ({Path.GetFileName(file)}:{line})";
    }

    /// <summary>
    /// Reduces a compiler-generated name such as <c>&lt;Load&gt;d__3</c>, <c>&lt;Load&gt;b__0_0</c> or
    /// <c>&lt;&lt;Main&gt;$&gt;b__0_0</c> to the user-written method name.
    /// </summary>
    /// <param name="name">The name to unwrap.</param>
    /// <returns>The innermost name in angle brackets, or <paramref name="name"/> when it is not generated.</returns>
    internal static string UnwrapGeneratedName(string name)
    {
        while (name.Length > 1 && name[0] == '<')
        {
            var depth = 0;
            var end = -1;
            for (var i = 0; i < name.Length && end < 0; i++)
            {
                depth += name[i] switch
                {
                    '<' => 1,
                    '>' => -1,
                    _ => 0,
                };
                if (depth == 0)
                {
                    end = i;
                }
            }

            if (end <= 1)
            {
                return name;
            }

            name = name[1..end];
        }

        return name.EndsWith('$') ? name[..^1] : name;
    }

    /// <summary>Whether a type belongs to a framework namespace or to the guard itself.</summary>
    /// <param name="type">The declaring type of a frame.</param>
    /// <returns><see langword="true"/> when the frame is not application code.</returns>
    private static bool IsFramework(Type type)
    {
        if (type.Assembly == GuardAssembly)
        {
            return true;
        }

        var ns = type.Namespace;
        if (ns is null)
        {
            return false;
        }

        if (ns == AspNetCoreNamespace)
        {
            return true;
        }

        foreach (var root in FrameworkNamespaces)
        {
            if (ns.StartsWith(root, StringComparison.Ordinal) && (ns.Length == root.Length || ns[root.Length] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the frame is a property accessor of the lazy-loaded entity (or one of its base types).</summary>
    /// <param name="method">The frame's method.</param>
    /// <param name="type">The frame's declaring type.</param>
    /// <param name="entityType">The lazy-loaded entity type, or <see langword="null"/> for commands.</param>
    /// <returns><see langword="true"/> when the frame should be skipped.</returns>
    private static bool IsEntityAccessor(MethodBase method, Type type, Type? entityType) =>
        entityType is not null && method.IsSpecialName && type.IsAssignableFrom(entityType);

    /// <summary>Whether a type was generated by the compiler (async state machine, closure, lambda cache).</summary>
    /// <param name="type">The type.</param>
    /// <returns><see langword="true"/> for names that start with <c>&lt;</c>.</returns>
    private static bool IsCompilerGenerated(Type type) => type.Name.StartsWith('<');

    /// <summary>Removes the generic arity suffix (<c>`1</c>) from a type name.</summary>
    /// <param name="name">The type name.</param>
    /// <returns>The name without the suffix.</returns>
    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }
}
