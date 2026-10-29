using Microsoft.EntityFrameworkCore;
using Trexon.Data;

await using var data = Data.Create(config => config
    .UseSqlite("Data Source=:memory:")
    .Entity<User>(entity =>
    {
        entity.ToTable("Users");

        // 1:1 — one profile per user; every profile belongs to one user.
        entity.HasOne(user => user.Profile)
            .WithOne(profile => profile.User)
            .HasForeignKey<UserProfile>(profile => profile.UserId);

        // 1:N — one author can have many posts.
        entity.HasMany(user => user.Posts)
            .WithOne(post => post.Author)
            .HasForeignKey(post => post.AuthorId);

        // N:M — users share roles; EF creates the UserRoles join table.
        entity.HasMany(user => user.Roles)
            .WithMany(role => role.Users)
            .UsingEntity("UserRoles");
    })
    .Entity<UserProfile>(entity => entity.ToTable("Profiles"))
    .Entity<Post>(entity => entity.ToTable("Posts"))
    .Entity<Role>(entity => entity.ToTable("Roles")));

var update = await data.UpdateAsync();
Console.WriteLine($"Schema changed: {update.Changed}");

// Save connected object graphs with a shared role, using one explicit save.
await using (var db = data.Open())
{
    var reader = new Role { Name = "Reader" };
    var author = new Role { Name = "Author" };

    db.Add(new User
    {
        Name = "Alice",
        IsActive = true,
        Profile = new UserProfile { Bio = "Writes about .NET" },
        Posts = [new Post { Title = "First post" }, new Post { Title = "Second post" }],
        Roles = [reader, author]
    });
    db.Add(new User
    {
        Name = "Bob",
        IsActive = false,
        Profile = new UserProfile { Bio = "Reads about databases" },
        Posts = [new Post { Title = "Database notes" }],
        Roles = [reader]
    });
    await db.SaveAsync();
}

// A fresh session loads all three relationships explicitly with Include.
await using (var db = data.Open())
{
    var users = await db.Table<User>().AsNoTracking()
        .Include(user => user.Profile)
        .Include(user => user.Posts)
        .Include(user => user.Roles)
        .AsSplitQuery()
        .OrderBy(user => user.Name)
        .ToListAsync();

    foreach (var user in users)
    {
        Console.WriteLine($"User: {user.Name}");
        Console.WriteLine($"  1:1 profile: {user.Profile?.Bio ?? "(none)"}");
        Console.WriteLine($"  1:N posts: {string.Join(", ", user.Posts.OrderBy(post => post.Title).Select(post => post.Title))}");
        Console.WriteLine($"  N:M roles: {string.Join(", ", user.Roles.OrderBy(role => role.Name).Select(role => role.Name))}");
    }

    // The reverse navigation shows that Reader is one role shared by two users.
    var reader = await db.Table<Role>().AsNoTracking()
        .Include(role => role.Users)
        .SingleAsync(role => role.Name == "Reader");
    Console.WriteLine($"Reader users: {string.Join(", ", reader.Users.OrderBy(user => user.Name).Select(user => user.Name))}");

    // Ordinary LINQ filters and projections are still available.
    var activeNames = await db.Table<User>().AsNoTracking()
        .Where(user => user.IsActive).OrderBy(user => user.Name)
        .Select(user => user.Name).ToListAsync();
    Console.WriteLine($"Active users: {string.Join(", ", activeNames)}");
}

Console.WriteLine($"Repeated update changed schema: {(await data.UpdateAsync()).Changed}");

public sealed class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public UserProfile? Profile { get; set; }
    public List<Post> Posts { get; set; } = [];
    public List<Role> Roles { get; set; } = [];
}

public sealed class UserProfile
{
    public int Id { get; set; }
    public string Bio { get; set; } = "";
    public int UserId { get; set; }
    public User User { get; set; } = null!;
}

public sealed class Post
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;
}

public sealed class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<User> Users { get; set; } = [];
}
