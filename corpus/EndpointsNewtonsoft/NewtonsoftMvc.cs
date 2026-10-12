using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Corpus.EndpointsNewtonsoft;

// MVC switched to Newtonsoft.Json: its controllers skip what Newtonsoft's [JsonIgnore] marks and read what only
// System.Text.Json's marks. Minimal APIs keep serializing with System.Text.Json.
public class Writer
{
    public int Id { get; set; }
    public string Name { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public virtual List<Novel> Novels { get; set; } = new();
}

public class Novel
{
    public int Id { get; set; }
    public string Title { get; set; }
    public int WriterId { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public virtual Writer Writer { get; set; }

    public int? GenreId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public virtual Genre Genre { get; set; }
}

public class Genre
{
    public int Id { get; set; }
    public string Label { get; set; }
}

public class LibraryDb(DbContextOptions<LibraryDb> options) : DbContext(options)
{
    public DbSet<Writer> Writers => Set<Writer>();
    public DbSet<Novel> Novels => Set<Novel>();
    public DbSet<Genre> Genres => Set<Genre>();
}

public static class MvcSetup
{
    public static void Configure(IServiceCollection services) => services.AddControllers().AddNewtonsoftJson();
}

[ApiController]
[Route("library")]
public class LibraryController(LibraryDb db) : ControllerBase
{
    [HttpGet("writers")]
    public async Task<List<Writer>> Writers() => await db.Writers.ToListAsync();

    [HttpGet("novels")]
    public async Task<List<Novel>> Novels() => await db.Novels.ToListAsync(); // expect: NP0005
}

public static class LibraryEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/writers", async (LibraryDb db) => await db.Writers.ToListAsync()); // expect: NP0005
        app.MapGet("/genres", async (LibraryDb db) => await db.Genres.ToListAsync());
    }
}
