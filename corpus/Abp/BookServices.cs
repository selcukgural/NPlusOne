using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Linq;
using Volo.Abp.Modularity;

namespace Corpus.Abp;

[DependsOn(typeof(Volo.Abp.Application.AbpDddApplicationModule))]
public class AbpCorpusModule : AbpModule;

// ABP's repositories run their queries in ABP's EF Core layer; this project sees only the interfaces.
public class BookQueries(IRepository<Book, int> books, IRepository<Author, int> authors, IAsyncQueryableExecuter executer)
{
    public async Task<int> GetByIdPerElement(List<int> ids)
    {
        var total = 0;
        foreach (var id in ids)
        {
            var author = await authors.GetAsync(id, includeDetails: false); // expect: NP0001
            total += author.Name.Length;
        }

        return total;
    }

    public async Task<int> FindByIdPerElement(List<int> ids)
    {
        var total = 0;
        foreach (var id in ids)
        {
            var author = await authors.FindAsync(id, includeDetails: false); // expect: NP0001
            total += author?.Name.Length ?? 0;
        }

        return total;
    }

    public async Task<int> RepositoryOperatorPerElement(List<int> ids)
    {
        var total = 0;
        foreach (var id in ids)
        {
            var author = await authors.FirstOrDefaultAsync(a => a.Id == id); // expect: NP0001
            total += author?.Name.Length ?? 0;
        }

        return total;
    }

    public async Task<int> CountPerElement(List<int> authorIds)
    {
        var total = 0;
        foreach (var id in authorIds)
        {
            total += await books.CountAsync(b => b.AuthorId == id); // expect: NP0001
        }

        return total;
    }

    public async Task<int> ListByPredicatePerElement(List<int> authorIds)
    {
        var total = 0;
        foreach (var id in authorIds)
        {
            total += (await books.GetListAsync(b => b.AuthorId == id)).Count; // expect: NP0001
        }

        return total;
    }

    public async Task<int> ExecuterPerElement(List<int> authorIds)
    {
        var total = 0;
        var queryable = await books.GetQueryableAsync();
        foreach (var id in authorIds)
        {
            total += await executer.CountAsync(queryable.Where(b => b.AuthorId == id)); // expect: NP0001
        }

        return total;
    }

    public async Task<int> QueryableOperatorPerElement(List<int> ids)
    {
        var total = 0;
        foreach (var id in ids)
        {
            var queryable = await authors.GetQueryableAsync();
            total += queryable.First(a => a.Id == id).Name.Length; // expect: NP0001
        }

        return total;
    }

    public async Task<int> NavigationPerElement()
    {
        var total = 0;
        foreach (var book in await books.GetListAsync())
        {
            total += book.Author.Name.Length; // expect: NP0002
        }

        return total;
    }

    public async Task<int> ExplicitLoadPerElement()
    {
        var total = 0;
        foreach (var book in await books.GetListAsync())
        {
            await books.EnsurePropertyLoadedAsync(book, b => b.Author); // expect: NP0001
            total += book.Author.Name.Length;
        }

        return total;
    }

    public async Task ExistsCheckPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            await authors.EnsureExistsAsync(id); // expect: NP0001
        }
    }

    public async Task<int> NavigationOfExplicitDetails()
    {
        var total = 0;
        var queryable = await books.WithDetailsAsync(b => b.Author);
        foreach (var book in await executer.ToListAsync(queryable))
        {
            total += book.Author.Name.Length;
        }

        return total;
    }

    public async Task<int> LoadedOnce(List<int> ids)
    {
        var total = 0;
        var all = await authors.GetListAsync();
        foreach (var id in ids)
        {
            total += all.First(a => a.Id == id).Name.Length;
        }

        return total;
    }

    public async Task<int> QueryRunOnce(List<int> authorIds)
    {
        var total = 0;
        var queryable = await books.GetQueryableAsync();
        var matching = await executer.ToListAsync(queryable.Where(b => authorIds.Contains(b.AuthorId)));
        foreach (var id in authorIds)
        {
            total += matching.Count(b => b.AuthorId == id);
        }

        return total;
    }

    public async Task<int> ExecuterOverMemory(List<int> ids)
    {
        var total = 0;
        var names = new List<string> { "a", "bb" };
        foreach (var id in ids)
        {
            total += await executer.CountAsync(names.AsQueryable().Where(n => n.Length == id));
        }

        return total;
    }
}

public class BookCommands(IRepository<Book, int> books, IBookRepository bookRepository)
{
    public async Task DeleteByIdPerElement(List<int> ids)
    {
        foreach (var id in ids)
        {
            await books.DeleteAsync(id); // expect: NP0001
        }
    }

    public async Task InsertWithoutSaving(List<Book> newBooks)
    {
        // ground-truth-arguments: new
        foreach (var book in newBooks)
        {
            book.AuthorId = 1;
            await books.InsertAsync(book);
        }
    }

    public async Task InsertSavingEach(List<Book> newBooks)
    {
        // ground-truth-arguments: new
        foreach (var book in newBooks)
        {
            book.AuthorId = 1;
            await books.InsertAsync(book, autoSave: true); // expect: NP0003
        }
    }

    // Its implementation is in the EF Core layer, which this project does not have, and its name does not match this
    // folder's nplusone.data_access_types: nothing says it queries. corpus/Layered has the guess (NP0006) for such a call.
    public async Task<int> CustomRepositoryMethodPerElement(List<int> ids)
    {
        var total = 0;
        foreach (var id in ids)
        {
            // ground-truth: the custom repository is implemented in the EF Core layer, which this project does not have
            total += (await bookRepository.GetCheaperThanAsync(id)).Count;
        }

        return total;
    }
}

// The application service base class hands out AsyncExecuter; CrudAppService hands out Repository the same way.
public class AuthorAppService(IRepository<Author, int> authors) : ApplicationService
{
    public virtual async Task<int> NamesPerElement(List<int> ids)
    {
        var total = 0;
        var queryable = await authors.GetQueryableAsync();
        foreach (var id in ids)
        {
            var author = await AsyncExecuter.FirstOrDefaultAsync(queryable.Where(a => a.Id == id)); // expect: NP0001
            total += author?.Name.Length ?? 0;
        }

        return total;
    }
}
