using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NPlusOne.Analyzers.Tests.Harness;

namespace NPlusOne.Analyzers.Tests;

// An IDE analyses a new compilation after every edit, and the analyzer reuses what depends only on declarations, and
// what unchanged files bind to, from the previous one. These edit one file at a time, as typing does, and check that an
// edit that changes the answer is never answered from the previous compilation. Each test has its own assembly name, so
// no other test's compilations are reused.
public class IncrementalTests
{
    private const string Model = """
        using Microsoft.EntityFrameworkCore;

        namespace Shop;

        public class Customer
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class Order
        {
            public int Id { get; set; }
            public int CustomerId { get; set; }
            public virtual Customer Customer { get; set; }
        }

        public class Author
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class Note
        {
            public int Id { get; set; }
            public int AuthorId { get; set; }
            public virtual Author Author { get; set; }
        }
        """;

    private const string NotesService = """
        namespace Shop;

        public class Notes(ShopDb db)
        {
            public int Total()
            {
                var total = 0;
                foreach (var note in db.Set<Note>().ToList())
                {
                    total += note.Author.Name.Length;
                }

                return total;
            }
        }
        """;

    private const string PricingService = """
        namespace Shop;

        public class Pricing(IPricer pricer)
        {
            public int Sum(List<int> ids)
            {
                var sum = 0;
                foreach (var id in ids)
                {
                    sum += pricer.Price(id);
                }

                return sum;
            }
        }
        """;

    [Fact]
    public async Task An_entity_set_added_in_another_file_makes_its_type_an_entity()
    {
        const string before = "public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options) { public DbSet<Order> Orders { get; set; } }";
        const string after = "public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options) { public DbSet<Order> Orders { get; set; } public DbSet<Note> Notes { get; set; } }";
        var compilation = Compile(("Model.cs", Model), ("Context.cs", Context(before)), ("Notes.cs", NotesService));
        Assert.Empty(await AnalyzeAsync(compilation));

        Assert.Equal(["Notes.cs:10 NP0002"], await AnalyzeAsync(Edit(compilation, "Context.cs", Context(after))));
    }

    [Fact]
    public async Task An_entity_set_added_after_a_method_was_typed_makes_its_type_an_entity()
    {
        // Typing a method keeps the type shapes, so the entities are reused; the entity set typed next changes them.
        var withMethod = Context(OrdersContext[..^1] + "public int Count() => 0; }");
        var withSet = withMethod.Replace("public int Count()", "public DbSet<Note> Notes { get; set; } public int Count()");
        var compilation = Compile(("Model.cs", Model), ("Context.cs", Context(OrdersContext)), ("Notes.cs", NotesService));
        Assert.Empty(await AnalyzeAsync(compilation));

        var typed = Edit(compilation, "Context.cs", withMethod);
        Assert.Empty(await AnalyzeAsync(typed));

        Assert.Equal(["Notes.cs:10 NP0002"], await AnalyzeAsync(Edit(typed, "Context.cs", withSet)));
    }

    [Fact]
    public async Task An_entity_mapped_in_a_model_creating_body_makes_its_type_an_entity()
    {
        // The edit is inside a method body, so the file's declarations stay the same.
        const string before = "public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options) { protected override void OnModelCreating(ModelBuilder modelBuilder) { modelBuilder.Entity<Order>(); } }";
        var after = before.Replace("modelBuilder.Entity<Order>();", "modelBuilder.Entity<Order>(); modelBuilder.Entity<Note>();");
        var compilation = Compile(("Model.cs", Model), ("Context.cs", Context(before)), ("Notes.cs", NotesService));
        Assert.Empty(await AnalyzeAsync(compilation));

        var added = Edit(compilation, "Context.cs", Context(after));
        Assert.Equal(["Notes.cs:10 NP0002"], await AnalyzeAsync(added));

        Assert.Empty(await AnalyzeAsync(Edit(added, "Context.cs", Context(before))));
    }

    [Fact]
    public async Task An_interface_moved_to_another_class_moves_the_calls_through_it()
    {
        const string before = """
            namespace Shop;

            public interface IPricer { int Price(int id); }

            public class DbPricer(ShopDb db) : IPricer { public int Price(int id) => db.Orders.Count(o => o.CustomerId == id); }

            public class FixedPricer { public int Price(int id) => id; }
            """;
        var after = before.Replace("DbPricer(ShopDb db) : IPricer", "DbPricer(ShopDb db)").Replace("class FixedPricer", "class FixedPricer : IPricer");
        var compilation = Compile(("Model.cs", Model), ("Context.cs", Context(OrdersContext)), ("Pricers.cs", before), ("Pricing.cs", PricingService));
        Assert.Equal(["Pricing.cs:10 NP0001"], await AnalyzeAsync(compilation));

        Assert.Empty(await AnalyzeAsync(Edit(compilation, "Pricers.cs", after)));
    }

    [Fact]
    public async Task An_interface_added_to_a_class_that_queries_makes_the_calls_through_it_query()
    {
        const string before = """
            namespace Shop;

            public interface IPricer { int Price(int id); }

            public class DbPricer(ShopDb db) { public int Price(int id) => db.Orders.Count(o => o.CustomerId == id); }

            public class FixedPricer : IPricer { public int Price(int id) => id; }
            """;
        var after = before.Replace("DbPricer(ShopDb db)", "DbPricer(ShopDb db) : IPricer");
        var compilation = Compile(("Model.cs", Model), ("Context.cs", Context(OrdersContext)), ("Pricers.cs", before), ("Pricing.cs", PricingService));
        Assert.Empty(await AnalyzeAsync(compilation));

        Assert.Equal(["Pricing.cs:10 NP0001"], await AnalyzeAsync(Edit(compilation, "Pricers.cs", after)));
    }

    [Fact]
    public async Task A_constant_changed_in_another_file_changes_an_unchanged_file_that_reads_it()
    {
        const string context = """
            public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
            {
                public DbSet<Order> Orders { get; set; }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                    => modelBuilder.Entity<Order>().Navigation(o => o.Customer).AutoInclude(Flags.IncludeCustomer);
            }
            """;
        const string service = """
            namespace Shop;

            public class Orders(ShopDb db)
            {
                public int Total()
                {
                    var total = 0;
                    foreach (var order in db.Orders.ToList())
                    {
                        total += order.Customer.Name.Length;
                    }

                    return total;
                }
            }
            """;
        const string flags = "namespace Shop; public static class Flags { public const bool IncludeCustomer = true; }";
        var compilation = Compile(("Model.cs", Model), ("Context.cs", Context(context)), ("Flags.cs", flags), ("Orders.cs", service));
        Assert.Empty(await AnalyzeAsync(compilation));

        Assert.Equal(["Orders.cs:10 NP0002"], await AnalyzeAsync(Edit(compilation, "Flags.cs", flags.Replace("true", "false"))));
    }

    [Fact]
    public async Task A_referenced_project_that_changes_its_entities_changes_the_analysis()
    {
        var data = "Data" + Guid.NewGuid().ToString("N");
        var before = Emit(data, ("Model.cs", Model), ("Context.cs", Context("public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options) { public DbSet<Note> Notes { get; set; } }")));
        var after = Emit(data, ("Model.cs", Model), ("Context.cs", Context("public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options) { public DbSet<Order> Orders { get; set; } }")));
        var compilation = Compile([("Notes.cs", NotesService)], before);
        Assert.Equal(["Notes.cs:10 NP0002"], await AnalyzeAsync(compilation));

        Assert.Empty(await AnalyzeAsync(compilation.ReplaceReference(before, after)!));
    }

    [Fact]
    public async Task A_partial_class_implements_the_interface_its_other_part_names()
    {
        const string pricers = """
            namespace Shop;

            public interface IPricer { int Price(int id); }

            public partial class DbPricer(ShopDb db) { public int Price(int id) => db.Orders.Count(o => o.CustomerId == id); }
            """;
        const string implements = "namespace Shop; public partial class DbPricer : IPricer { }";
        var compilation = Compile(("Model.cs", Model), ("Context.cs", Context(OrdersContext)), ("Pricers.cs", pricers), ("Implements.cs", implements), ("Pricing.cs", PricingService));

        Assert.Equal(["Pricing.cs:10 NP0001"], await AnalyzeAsync(compilation));
    }

    [Fact]
    public async Task A_nested_class_implements_an_interface()
    {
        const string pricers = """
            namespace Shop;

            public interface IPricer { int Price(int id); }

            public static class Pricers
            {
                public class DbPricer(ShopDb db) : IPricer { public int Price(int id) => db.Orders.Count(o => o.CustomerId == id); }
            }
            """;
        var compilation = Compile(("Model.cs", Model), ("Context.cs", Context(OrdersContext)), ("Pricers.cs", pricers), ("Pricing.cs", PricingService));

        Assert.Equal(["Pricing.cs:10 NP0001"], await AnalyzeAsync(compilation));
    }

    [Fact]
    public async Task A_partial_class_overrides_in_its_other_part()
    {
        const string pricers = """
            namespace Shop;

            public abstract class Pricer { public abstract int Price(int id); }

            public partial class DbPricer(ShopDb db) : Pricer { private readonly ShopDb _db = db; }
            """;
        const string overrides = "namespace Shop; public partial class DbPricer { public override int Price(int id) => _db.Orders.Count(o => o.CustomerId == id); }";
        var compilation = Compile(("Model.cs", Model), ("Context.cs", Context(OrdersContext)), ("Pricers.cs", pricers), ("Overrides.cs", overrides), ("Pricing.cs", PricingService.Replace("IPricer", "Pricer")));

        Assert.Equal(["Pricing.cs:10 NP0001"], await AnalyzeAsync(compilation));
    }

    private const string OrdersContext = "public class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options) { public DbSet<Order> Orders { get; set; } }";

    private static string Context(string body) => "using Microsoft.EntityFrameworkCore;\n\nnamespace Shop;\n\n" + body;

    private static CSharpCompilation Compile(params (string Path, string Text)[] files) => Compile(files, null);

    private static CSharpCompilation Compile((string Path, string Text)[] files, MetadataReference? reference)
        => CSharpCompilation.Create(
            "Incremental" + Guid.NewGuid().ToString("N"),
            [.. files.Select(f => CSharpSyntaxTree.ParseText(f.Text, TestCompilation.ParseOptions, f.Path)), CSharpSyntaxTree.ParseText(TestCompilation.GlobalUsings, TestCompilation.ParseOptions, "GlobalUsings.cs")],
            reference is null ? TestCompilation.References : TestCompilation.References.Add(reference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

    private static PortableExecutableReference Emit(string name, params (string Path, string Text)[] files)
    {
        var compilation = Compile(files).WithAssemblyName(name);
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray(), filePath: name + ".dll");
    }

    /// <summary>Replaces one file the way an editor does: the other trees, and the references, stay the same objects.</summary>
    private static Compilation Edit(Compilation compilation, string path, string text)
    {
        var tree = compilation.SyntaxTrees.Single(t => t.FilePath == path);
        return compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(text, TestCompilation.ParseOptions, path));
    }

    private static async Task<string[]> AnalyzeAsync(Compilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        var options = new AnalyzerOptions([], new DictionaryOptionsProvider(new Dictionary<string, string>()));
        var diagnostics = await compilation.WithAnalyzers([new NPlusOneAnalyzer()], options).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
        return [.. diagnostics.Select(d => $"{d.Location.SourceTree!.FilePath}:{d.Location.GetLineSpan().StartLinePosition.Line + 1} {d.Id}").Order(StringComparer.Ordinal)];
    }
}
