using System.Text.RegularExpressions;

namespace Phub.Api.Tests.Quality;

public sealed class MigrationsConsistencyTests
{
    // Legacy migration created manually before EF metadata policy.
    private static readonly HashSet<string> LegacyMigrationsWithoutDesigner = new(StringComparer.OrdinalIgnoreCase)
    {
        "20260214130000_ClientOnboarding"
    };

    [Fact]
    public void EveryMigrationFile_MustHaveDesignerAndMigrationAttribute()
    {
        var migrationsDirectory = Path.Combine(FindRepositoryRoot(), "src", "Phub.Infrastructure", "Migrations");
        Assert.True(Directory.Exists(migrationsDirectory), $"Migrations directory not found: {migrationsDirectory}");

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var fileName = Path.GetFileName(path);
                return !fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                       && !string.Equals(fileName, "AppDbContextModelSnapshot.cs", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(migrationFiles);

        foreach (var migrationPath in migrationFiles)
        {
            var baseName = Path.GetFileNameWithoutExtension(migrationPath);
            if (LegacyMigrationsWithoutDesigner.Contains(baseName))
            {
                continue;
            }

            var designerPath = Path.Combine(migrationsDirectory, $"{baseName}.Designer.cs");
            Assert.True(File.Exists(designerPath), $"Missing designer file for migration '{baseName}'.");

            var migrationId = Regex.Match(baseName, @"^\d+").Value;
            Assert.False(string.IsNullOrWhiteSpace(migrationId), $"Invalid migration filename format: {baseName}");

            var designerContent = File.ReadAllText(designerPath);
            Assert.Contains($"[Migration(\"{migrationId}", designerContent, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PhubHub.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing PhubHub.sln.");
    }
}
