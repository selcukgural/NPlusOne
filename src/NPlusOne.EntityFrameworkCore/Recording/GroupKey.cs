namespace NPlusOne.EntityFrameworkCore.Recording;

/// <summary>
/// Identifies one group of repeated work in a scope. Two runs land in the same group when they have
/// the same kind, the same normalized SQL (or navigation) and the same call site.
/// </summary>
/// <param name="Kind">Query, write or lazy load.</param>
/// <param name="Text">The SQL fingerprint for commands, <c>Entity.Navigation</c> for lazy loads.</param>
/// <param name="CallSite">The call site, or <see langword="null"/> when not captured or unknown.</param>
internal readonly record struct GroupKey(NPlusOneSuspectKind Kind, string Text, string? CallSite);
