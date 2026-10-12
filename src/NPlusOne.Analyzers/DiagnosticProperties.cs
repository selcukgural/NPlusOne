namespace NPlusOne.Analyzers;

/// <summary>
/// Keys of the properties diagnostics carry for the code fixes. Public because the code fixes live
/// in another assembly, so that the analyzer itself never references the Workspaces layer.
/// </summary>
public static class DiagnosticProperties
{
    /// <summary>NP0002: the navigation path to include, such as <c>Lines.Product</c>; the first additional location is the query's root.</summary>
    public const string IncludePath = "IncludePath";

    /// <summary>NP0003: present when the <c>SaveChanges</c> call sits in a loop statement and can move after it.</summary>
    public const string MovableSave = "MovableSave";
}
