using Microsoft.CodeAnalysis;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>Where a value is handed to a serializer (NP0005), carried down into the containers it is built from.</summary>
/// <param name="Reported">Where to report: the returned or serialized expression.</param>
/// <param name="Subject">How the message starts: what hands the value to the serializer.</param>
/// <param name="Serializer">The serializer that walks the value; navigations and properties it ignores are never read.</param>
/// <param name="Item">
/// Whether the value is one item of a stream (<c>yield return</c>, a channel write): one of many entities the serializer
/// writes, so its own navigations count once per item, not once.
/// </param>
internal sealed record SerializedSite(IOperation Reported, string Subject, JsonSerializerKind Serializer, bool Item = false);
