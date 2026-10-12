using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>Proof that a call makes a round trip: the effect, and the chain of methods that leads to it.</summary>
/// <param name="Kind">Query or save.</param>
/// <param name="Operator">The operator that runs it, such as <c>ToListAsync</c>.</param>
/// <param name="Chain">The methods from the called one down to the one whose body runs it (<c>LoadOrders</c>, <c>OrderRepository.Get</c>).</param>
/// <param name="Shape">What the query looks like.</param>
/// <param name="Location">Where the effect is, shown as an additional location.</param>
internal sealed record Witness(EffectKind Kind, string Operator, ImmutableArray<string> Chain, QueryShape Shape, Location? Location)
{
    /// <summary>The same witness reached through one more call.</summary>
    /// <param name="method">The method the caller called.</param>
    public Witness Through(string method) => this with { Chain = Chain.Insert(0, method) };
}
