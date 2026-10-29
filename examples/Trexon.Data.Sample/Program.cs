using Microsoft.EntityFrameworkCore;
using Trexon.Data;

await using var data = Data.Create(config => config
    .UseSqlite("Data Source=:memory:")
    .Entity<User>());

var update = await data.UpdateAsync();
Console.WriteLine($"Schema changed: {update.Changed}");

await using (var db = data.Open())
{
    db.Add(new User { Name = "Alice", IsActive = true });
    db.Add(new User { Name = "Bob", IsActive = false });
    await db.SaveAsync();
}

await using (var db = data.Open())
{
    var users = await db.Table<User>().AsNoTracking()
        .Where(user => user.IsActive).OrderBy(user => user.Name)
        .Select(user => user.Name).ToListAsync();
    foreach (var name in users) Console.WriteLine(name);
}
Console.WriteLine($"Repeated update changed schema: {(await data.UpdateAsync()).Changed}");

public sealed class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}
