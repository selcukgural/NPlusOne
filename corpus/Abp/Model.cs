using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;

namespace Corpus.Abp;

// The domain layer of an ABP application: entities, and a custom repository whose implementation lives in the EF Core
// layer, which the application layer does not reference.
public class Author : Entity<int>
{
    public string Name { get; set; }
}

public class Book : Entity<int>
{
    public string Title { get; set; }
    public decimal Price { get; set; }
    public int AuthorId { get; set; }
    public virtual Author Author { get; set; }
}

public interface IBookRepository : IRepository<Book, int>
{
    Task<List<Book>> GetCheaperThanAsync(decimal price);
}
