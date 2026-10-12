using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NPlusOne.Analyzers.Analysis;

/// <summary>The call a lambda or method group is passed to, and the parameter it is passed as.</summary>
/// <param name="Invocation">The call.</param>
/// <param name="Parameter">The delegate parameter the callback binds to.</param>
internal sealed record CallbackSite(IInvocationOperation Invocation, IParameterSymbol Parameter)
{
    /// <summary>
    /// Where a callback goes: walks up through delegate creation and conversions to the argument
    /// and its call. Null when the delegate is stored, returned or passed to a constructor instead.
    /// </summary>
    /// <param name="callback">The lambda, or the delegate creation of a method group.</param>
    public static CallbackSite? Of(IOperation callback)
    {
        var current = callback.Parent;
        while (current is IDelegateCreationOperation or IConversionOperation)
        {
            current = current.Parent;
        }

        if (current is IArgumentOperation { Parameter: { } parameter, Parent: IInvocationOperation invocation })
        {
            return new CallbackSite(invocation, parameter);
        }

        return null;
    }
}
