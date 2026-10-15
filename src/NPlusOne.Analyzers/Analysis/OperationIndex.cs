using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>
/// What the walks over a whole method look for, found in one walk per operation tree: the operations that write each local
/// (declarators, assignments, <c>??=</c>, the <c>foreach</c> loops declaring it, deconstructions and declaration
/// expressions declaring it), where lazy loading is first turned off, and the explicit loads. Tracing a local, finding its
/// loop or its single initializer, and the lazy-load checks each walked the whole method before, once per question, which
/// made large methods quadratic.
/// </summary>
/// <remarks>
/// Kept per root operation in a <see cref="ConditionalWeakTable{TKey,TValue}"/>: the index lives as long as the tree, so
/// nothing outlives the compilation. The lists keep the walk's order, so a question answered from them sees the operations
/// in the order the walk saw them. Each question still tests the operations with its own conditions: the index only
/// narrows them down to the ones that can concern the local.
/// </remarks>
internal sealed class OperationIndex
{
    /// <summary>The index of each operation tree, by its root.</summary>
    private static readonly ConditionalWeakTable<IOperation, OperationIndex> Indexes = new();

    /// <summary>Builds an index; kept in a field so a lookup allocates no delegate.</summary>
    private static readonly ConditionalWeakTable<IOperation, OperationIndex>.CreateValueCallback Build = static root => new OperationIndex(root);

    /// <summary>The operations that may write each local, in walk order.</summary>
    private readonly Dictionary<ILocalSymbol, List<IOperation>> _writes = new(SymbolEqualityComparer.Default);

    /// <summary>The calls that may load a navigation explicitly (<c>Load</c>, <c>LoadAsync</c>, ABP's <c>Ensure*LoadedAsync</c>), in walk order.</summary>
    private readonly List<IInvocationOperation> _explicitLoads = [];

    /// <summary>Creates the index of a tree in one walk.</summary>
    /// <param name="root">The root of the tree.</param>
    private OperationIndex(IOperation root)
    {
        foreach (var operation in root.Descendants())
        {
            switch (operation)
            {
                case IVariableDeclaratorOperation declarator:
                    Add(declarator.Symbol, declarator);
                    break;
                case ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment:
                    Add(target.Local, assignment);
                    break;
                case ISimpleAssignmentOperation assignment when IsLazyLoadingOff(assignment):
                    LazyLoadingOff = Math.Min(LazyLoadingOff, assignment.Syntax.SpanStart);
                    break;
                case ICoalesceAssignmentOperation { Target: ILocalReferenceOperation target } coalesce:
                    Add(target.Local, coalesce);
                    break;
                case IForEachLoopOperation loop:
                    foreach (var local in loop.Locals)
                    {
                        Add(local, loop);
                    }

                    break;
                case IDeconstructionAssignmentOperation or IDeclarationExpressionOperation:
                    AddDeclared(operation);
                    break;
                case IInvocationOperation { TargetMethod.Name: "Load" or "LoadAsync" or "EnsurePropertyLoadedAsync" or "EnsureCollectionLoadedAsync" } invocation:
                    _explicitLoads.Add(invocation);
                    break;
            }
        }
    }

    /// <summary>Where the first <c>LazyLoadingEnabled = false</c> of the tree starts, or <see cref="int.MaxValue"/> without one.</summary>
    public int LazyLoadingOff { get; private set; } = int.MaxValue;

    /// <summary>The calls that may load a navigation explicitly, in walk order; the caller tests their shape.</summary>
    public IReadOnlyList<IInvocationOperation> ExplicitLoads => _explicitLoads;

    /// <summary>The index of the tree an operation belongs to, built on first use.</summary>
    /// <param name="anyOperation">Any operation of the tree.</param>
    public static OperationIndex Of(IOperation anyOperation) => Indexes.GetValue(Operations.Root(anyOperation), Build);

    /// <summary>
    /// The operations that may write <paramref name="local"/>, in walk order: its declarators, plain and <c>??=</c>
    /// assignments to it, the <c>foreach</c> loops declaring it, and the deconstructions and declaration expressions that
    /// declare it.
    /// </summary>
    /// <param name="local">The local.</param>
    public IReadOnlyList<IOperation> WritesOf(ILocalSymbol local) => _writes.TryGetValue(local, out var writes) ? writes : [];

    /// <summary>Whether an assignment turns lazy loading off: <c>ChangeTracker.LazyLoadingEnabled = false</c>.</summary>
    /// <param name="assignment">The assignment.</param>
    private static bool IsLazyLoadingOff(ISimpleAssignmentOperation assignment)
        => assignment is { Target: IPropertyReferenceOperation { Property.Name: "LazyLoadingEnabled" }, Value.ConstantValue: { HasValue: true, Value: false } };

    /// <summary>Adds an operation to the writes of a local.</summary>
    /// <param name="local">The local.</param>
    /// <param name="operation">The operation.</param>
    private void Add(ILocalSymbol local, IOperation operation)
    {
        if (!_writes.TryGetValue(local, out var writes))
        {
            writes = [];
            _writes.Add(local, writes);
        }

        writes.Add(operation);
    }

    /// <summary>Adds a deconstruction or declaration expression to the writes of every local it declares.</summary>
    /// <param name="operation">The operation.</param>
    private void AddDeclared(IOperation operation)
    {
        foreach (var descendant in operation.Descendants())
        {
            if (descendant is ILocalReferenceOperation { IsDeclaration: true } reference)
            {
                var writes = WritesOf(reference.Local);
                if (writes.Count == 0 || !ReferenceEquals(writes[writes.Count - 1], operation))
                {
                    Add(reference.Local, operation);
                }
            }
        }
    }
}
