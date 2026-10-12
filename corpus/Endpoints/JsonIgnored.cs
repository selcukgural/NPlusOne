using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Corpus.Endpoints;

// A navigation the serializer is told to skip is never read, so it never loads. System.Text.Json's
// [JsonIgnore] skips it unless a condition says otherwise: WhenWritingNull and WhenWritingDefault read the
// value to compare it, which loads it. Newtonsoft's [JsonIgnore] only binds Newtonsoft, and System.Text.Json's
// only System.Text.Json; ASP.NET Core serializes with System.Text.Json by default.
public class Writer
{
    public int Id { get; set; }
    public string Name { get; set; }

    [JsonIgnore]
    public virtual List<Book> Books { get; set; } = new();
}

public class Shelf
{
    public int Id { get; set; }
    public string Name { get; set; }
    public virtual List<Book> Books { get; set; } = new();
}

public class Book
{
    public int Id { get; set; }
    public string Title { get; set; }
    public int WriterId { get; set; }
    public int ShelfId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public virtual Writer Writer { get; set; }
}

public class Review
{
    public int Id { get; set; }
    public int BookId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public virtual Book Book { get; set; }
}

#if NET10_0_OR_GREATER
// .NET 10's WhenWriting leaves the property out of the output, but reads it first: the navigation still loads.
public class Translation
{
    public int Id { get; set; }
    public int BookId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public virtual Book Book { get; set; }
}
#endif

public class Edition
{
    public int Id { get; set; }
    public int BookId { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public virtual Book Book { get; set; }
}

// Newtonsoft also honours [IgnoreDataMember]; System.Text.Json does not.
public class Printing
{
    public int Id { get; set; }
    public int BookId { get; set; }

    [IgnoreDataMember]
    public virtual Book Book { get; set; }
}

public class LibraryDb : DbContext
{
    public LibraryDb(DbContextOptions<LibraryDb> options) : base(options) { }
    public DbSet<Writer> Writers => Set<Writer>();
    public DbSet<Shelf> Shelves => Set<Shelf>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Edition> Editions => Set<Edition>();
    public DbSet<Printing> Printings => Set<Printing>();
#if NET10_0_OR_GREATER
    public DbSet<Translation> Translations => Set<Translation>();
#endif
}

[ApiController]
[Route("library")]
public class LibraryController : ControllerBase
{
    private readonly LibraryDb _db;
    public LibraryController(LibraryDb db) => _db = db;

    [HttpGet("books")]
    public async Task<List<Book>> Books() => await _db.Books.ToListAsync();

    [HttpGet("writers")]
    public async Task<List<Writer>> Writers() => await _db.Writers.ToListAsync();

    // One shelf: its books load in one query, and the writer of each book is ignored.
    [HttpGet("shelves/{id}")]
    public async Task<Shelf> Shelf(int id) => await _db.Shelves.FirstAsync(s => s.Id == id);

    // Books are included; the writer of each book is ignored, so nothing loads per book.
    [HttpGet("shelves")]
    public async Task<List<Shelf>> Shelves() => await _db.Shelves.Include(s => s.Books).ToListAsync();

    [HttpGet("shelves/plain")]
    public async Task<List<Shelf>> ShelvesWithoutBooks() => await _db.Shelves.ToListAsync(); // expect: NP0005

    [HttpGet("reviews")]
    public async Task<List<Review>> Reviews() => await _db.Reviews.ToListAsync(); // expect: NP0005

#if NET10_0_OR_GREATER
    [HttpGet("translations")]
    public async Task<List<Translation>> Translations() => await _db.Translations.ToListAsync(); // expect: NP0005
#endif

    // Newtonsoft's attribute does not bind System.Text.Json, which ASP.NET Core uses unless told otherwise.
    [HttpGet("editions")]
    public async Task<List<Edition>> Editions() => await _db.Editions.ToListAsync(); // expect: NP0005
}

public class LibraryExport
{
    private static readonly JsonSerializerOptions Json = new() { ReferenceHandler = ReferenceHandler.IgnoreCycles };
    private static readonly Newtonsoft.Json.JsonSerializerSettings JsonNet = new() { ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore };

    private readonly LibraryDb _db;
    public LibraryExport(LibraryDb db) => _db = db;

    public async Task<string> BooksToJson()
    {
        var books = await _db.Books.ToListAsync();
        return JsonSerializer.Serialize(books, Json);
    }

    public async Task<string> EditionsWithNewtonsoft()
    {
        var editions = await _db.Editions.ToListAsync();
        return Newtonsoft.Json.JsonConvert.SerializeObject(editions, JsonNet);
    }

    public async Task<string> EditionsToJson()
    {
        var editions = await _db.Editions.ToListAsync();
        return JsonSerializer.Serialize(editions, Json); // expect: NP0005
    }

    public async Task<string> PrintingsWithNewtonsoft()
    {
        var printings = await _db.Printings.ToListAsync();
        return Newtonsoft.Json.JsonConvert.SerializeObject(printings, JsonNet);
    }

    public async Task<string> PrintingsToJson()
    {
        var printings = await _db.Printings.ToListAsync();
        return JsonSerializer.Serialize(printings, Json); // expect: NP0005
    }

    // System.Text.Json's attribute does not bind Newtonsoft: it serializes each book's writer.
    public async Task<string> BooksWithNewtonsoft()
    {
        var books = await _db.Books.ToListAsync();
        return Newtonsoft.Json.JsonConvert.SerializeObject(books, JsonNet); // expect: NP0005
    }
}
