using Microsoft.EntityFrameworkCore;

namespace Corpus.Lazy;

// Navigations whose names start alike: each read is matched to the navigation with exactly its name.
public class Issue
{
    public int Id { get; set; }
    public int AssigneeId { get; set; }
    public virtual Member Assignee { get; set; }
    public int AuthorId { get; set; }
    public virtual Member Author { get; set; }
}

public class Member
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class IssueDb : DbContext
{
    public IssueDb(DbContextOptions<IssueDb> options) : base(options) { }
    public DbSet<Issue> Issues => Set<Issue>();
}

public class SimilarNavigations
{
    private readonly IssueDb _db;

    public SimilarNavigations(IssueDb db) => _db = db;

    public async Task<int> AuthorsOfAssignedIssues()
    {
        var issues = await _db.Issues.Include(i => i.Assignee).ToListAsync();
        var length = 0;
        foreach (var issue in issues)
        {
            length += issue.Assignee.Name.Length + issue.Author.Name.Length; // expect: NP0002
        }

        return length;
    }

    public async Task<int> AssigneesOfIssues()
    {
        var issues = await _db.Issues.Include(i => i.Assignee).ToListAsync();
        var length = 0;
        foreach (var issue in issues)
        {
            length += issue.Assignee.Name.Length;
        }

        return length;
    }
}
