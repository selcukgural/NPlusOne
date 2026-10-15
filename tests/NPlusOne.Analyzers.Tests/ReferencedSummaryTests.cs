using NPlusOne.Analyzers.Tests.Harness;

namespace NPlusOne.Analyzers.Tests;

// Summaries are read from every referenced assembly, so a package can carry any text in them: a broken or hostile
// summary (unknown kinds, empty fields, parameters the method does not have) must not crash the analyzer (AD0001 fails
// builds that treat warnings as errors) or make it report a call, and the well-formed summaries next to it keep working.
public sealed class ReferencedSummaryTests
{
    private const string Library = """
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("", "complete:1")]
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("M:Library.Store.Query(System.Int32)~System.Object", "Q|E|FirstOrDefault|Rows|||0")]
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("M:Library.Store.EmptySource(System.Int32)~System.Object", "Q||FirstOrDefault|Rows|||0")]
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("M:Library.Store.MissingParameter(System.Object)~System.Object", "Q|P7|FirstOrDefault|Rows|||0")]
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("M:Library.Store.NegativeParameter(System.Object)~System.Object", "S|P-3|SaveChanges|Rows|||0")]
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("M:Library.Store.NumericShape(System.Int32)~System.Object", "Q|E|FirstOrDefault|99|||0")]
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("M:Library.Store.Read(System.Object)~System.String", "N|9|Customer|1")]
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("M:Library.Store.Each(System.Collections.Generic.List{System.Object},System.Action{System.Object})~System.Object", "D|5")]
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("M:Library.Store.Pending~System.Collections.Generic.List{System.Object}", "R|Pending|0|1|0|0|0||")]
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("M:Library.Store.Numeric~System.Collections.Generic.List{System.Object}", "R|99|0|1|0|0|0||")]
        [assembly: NPlusOne.Generated.NPlusOneSummaryForgedAttribute("M:Library.Store.OutOfRange~System.Collections.Generic.List{System.Object}", "R|Parameter|4|1|0|0|0||")]

        namespace NPlusOne.Generated
        {
            [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
            internal sealed class NPlusOneSummaryForgedAttribute(string member, string facts) : System.Attribute;
        }

        namespace Library
        {
            public sealed class Store
            {
                public object Query(int id) => null;
                public object EmptySource(int id) => null;
                public object MissingParameter(object item) => null;
                public object NegativeParameter(object item) => null;
                public object NumericShape(int id) => null;
                public string Read(object item) => null;
                public object Each(List<object> items, Action<object> action) => null;
                public List<object> Pending() => [];
                public List<object> Numeric() => [];
                public List<object> OutOfRange() => [];
            }
        }
        """;

    private const string Consumer = """
        namespace Consumer;

        public static class Calls
        {
            public static void Run(Library.Store store, List<int> ids, List<object> items)
            {
                foreach (var id in ids)
                {
                    store.Query(id);
                    store.EmptySource(id);
                    store.NumericShape(id);
                }

                foreach (var item in items)
                {
                    store.MissingParameter(item);
                    store.NegativeParameter(item);
                    store.Read(item);
                }

                store.Each(items, item => store.Read(item));
                foreach (var row in store.Pending()) { store.Read(row); }
                foreach (var row in store.Numeric()) { store.Read(row); }
                foreach (var row in store.OutOfRange()) { store.Read(row); }
            }
        }
        """;

    [Fact]
    public async Task Broken_summaries_are_skipped_and_well_formed_ones_still_report()
    {
        var options = new Dictionary<string, string> { ["build_property.NPlusOneEmitSummaries"] = "false" };

        var diagnostics = await TestCompilation.AnalyzeProjectsAsync(
            [new TestCompilation.Project("Library", [new TestCompilation.Source("Store.cs", Library)]),
             new TestCompilation.Project("Consumer", [new TestCompilation.Source("Calls.cs", Consumer)])],
            options);

        Assert.Equal(
            ["Calls.cs:9 NP0001"],
            diagnostics.Select(d => $"{d.Location.SourceTree!.FilePath}:{d.Location.GetLineSpan().StartLinePosition.Line + 1} {d.Id}").Order(StringComparer.Ordinal));
    }
}
