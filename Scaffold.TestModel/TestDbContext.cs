using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Scaffold.TestModel;

public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Publisher> Publishers => Set<Publisher>();
    public DbSet<ShelfPosition> ShelfPositions => Set<ShelfPosition>();
    public DbSet<Award> Awards => Set<Award>();
    public DbSet<BookAward> BookAwards => Set<BookAward>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Book>(e =>
        {
            e.HasOne(x => x.Author)
                .WithMany(a => a.Books)
                .HasForeignKey(x => x.AuthorId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict: deleting a publisher must fail rather than cascade, so
            // the generated Delete action needs its DbUpdateException path.
            e.HasOne(x => x.Publisher)
                .WithMany(p => p.Books)
                .HasForeignKey(x => x.PublisherId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Review>()
            .HasOne(x => x.Book)
            .WithMany(b => b.Reviews)
            .HasForeignKey(x => x.BookId)
            .OnDelete(DeleteBehavior.Cascade);

        // Implicit join entity, no DbSet. Appears only as a skip navigation.
        builder.Entity<Book>()
            .HasMany(b => b.Genres)
            .WithMany(g => g.Books);

        builder.Entity<ShelfPosition>()
            .HasKey(x => new { x.ShelfId, x.Slot });

        // Explicit join: the composite key parts are themselves foreign keys.
        builder.Entity<BookAward>(e =>
        {
            e.HasKey(x => new { x.BookId, x.AwardId });

            e.HasOne(x => x.Book).WithMany().HasForeignKey(x => x.BookId);
            e.HasOne(x => x.Award).WithMany(a => a.BookAwards).HasForeignKey(x => x.AwardId);
        });
    }
}

/// <summary>
/// Present so the probe takes its preferred resolution path rather than the
/// dummy-connection fallback. Never opens a connection.
/// </summary>
public class TestDbContextFactory : IDesignTimeDbContextFactory<TestDbContext>
{
    public TestDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer("Server=(local);Database=ScaffoldTestModel;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}
