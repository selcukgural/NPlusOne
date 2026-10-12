using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Corpus.LazyLoader;

// Lazy loading without proxies (EF Core docs, "Lazy loading without proxies"): only the navigations
// whose getter asks the injected loader load lazily; a plain auto-property stays null until included.
public class Blog
{
    private ICollection<Post> _posts;
    private Author _author;

    public Blog() { }

    private Blog(ILazyLoader lazyLoader) => LazyLoader = lazyLoader;

    private ILazyLoader LazyLoader { get; set; }

    public int Id { get; set; }
    public string Name { get; set; }

    public ICollection<Post> Posts
    {
        get => LazyLoader.Load(this, ref _posts);
        set => _posts = value;
    }

    public Author Author
    {
        get => LazyLoader.Load(this, ref _author);
        set => _author = value;
    }

    public Theme Theme { get; set; }
}

public class Post
{
    private Action<object, string> _lazyLoader;
    private Blog _blog;

    public Post() { }

    private Post(Action<object, string> lazyLoader) => _lazyLoader = lazyLoader;

    public int Id { get; set; }
    public string Title { get; set; }

    public Blog Blog
    {
        get => _lazyLoader.Load(this, ref _blog);
        set => _blog = value;
    }

    public Author Editor { get; set; }
}

public class Author
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class Theme
{
    public int Id { get; set; }
    public string Color { get; set; }
}

public static class PocoLoadingExtensions
{
    public static TRelated Load<TRelated>(this Action<object, string> loader, object entity, ref TRelated navigationField, [System.Runtime.CompilerServices.CallerMemberName] string navigationName = null)
        where TRelated : class
    {
        loader?.Invoke(entity, navigationName);
        return navigationField;
    }
}

public class BlogDb(DbContextOptions<BlogDb> options) : DbContext(options)
{
    public DbSet<Blog> Blogs => Set<Blog>();
    public DbSet<Post> Posts => Set<Post>();
}

public class BlogReports(BlogDb db)
{
    public async Task LoaderNavigationsPerElement()
    {
        var blogs = await db.Blogs.ToListAsync();
        foreach (var blog in blogs)
        {
            Console.WriteLine(blog.Posts.Count); // expect: NP0002
            Console.WriteLine(blog.Author.Name); // expect: NP0002
        }
    }

    public async Task PlainNavigationPerElement()
    {
        var blogs = await db.Blogs.ToListAsync();
        foreach (var blog in blogs)
        {
            Console.WriteLine(blog.Theme?.Color);
        }
    }

    public async Task DelegateLoaderPerElement()
    {
        var posts = await db.Posts.ToListAsync();
        foreach (var post in posts)
        {
            Console.WriteLine(post.Blog.Name); // expect: NP0002
            Console.WriteLine(post.Editor?.Name);
        }
    }

    public async Task IncludedPerElement()
    {
        var blogs = await db.Blogs.Include(b => b.Posts).Include(b => b.Author).ToListAsync();
        foreach (var blog in blogs)
        {
            Console.WriteLine(blog.Posts.Count + blog.Author.Name);
        }
    }

    public async Task SharedModelIsNotLazyWithoutProxies(AppDb shop)
    {
        var orders = await shop.Orders.ToListAsync();
        foreach (var order in orders)
        {
            Console.WriteLine(order.Customer.Name);
        }
    }
}
