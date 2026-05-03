using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Phub.Application.Abstractions;
using Phub.Application.Services;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Infrastructure.Persistence;
using Phub.Infrastructure.Services;

namespace Phub.Api.Tests;

/// <summary>
/// Testes unitários para AdminCategoryService.
/// Usa InMemoryDatabase + DistributedCacheService com MemoryDistributedCache
/// (mesma condição do ambiente de dev sem Redis).
/// </summary>
public sealed class AdminCategoryServiceTests
{
    // ── factory helpers ───────────────────────────────────────────────────────

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static ICacheService CreateCache() =>
        new DistributedCacheService(
            new MemoryDistributedCache(
                Microsoft.Extensions.Options.Options.Create(
                    new MemoryDistributedCacheOptions())),
            NullLogger<DistributedCacheService>.Instance);

    private static AdminCategoryService CreateService(AppDbContext db, ICacheService? cache = null) =>
        new(db, cache ?? CreateCache());

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValidRequest_PersistsAndReturnsCategory()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.CreateAsync(
            new() { Name = "Eletrônicos", Slug = "eletronicos", IsActive = true },
            actorUserId: Guid.NewGuid(),
            tenantId: "t1");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("eletronicos", result.Data!.Slug);
        Assert.Equal(1, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task Create_DuplicateSlug_ReturnsValidationError()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        await svc.CreateAsync(new() { Name = "A", Slug = "same-slug", IsActive = true }, actor, "t1");
        var second = await svc.CreateAsync(new() { Name = "B", Slug = "same-slug", IsActive = true }, actor, "t1");

        Assert.False(second.Succeeded);
        Assert.Contains(second.Errors, e => e.Field == "slug");
    }

    [Fact]
    public async Task Create_InvalidSlugFormat_ReturnsValidationError()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.CreateAsync(
            new() { Name = "X", Slug = "UPPER CASE WITH SPACES", IsActive = true },
            Guid.NewGuid(), "t1");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == "slug");
    }

    [Fact]
    public async Task Create_WithValidParent_SetsParentId()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        var parent = await svc.CreateAsync(new() { Name = "Parent", Slug = "parent", IsActive = true }, actor, "t1");
        Assert.True(parent.Succeeded);

        var child = await svc.CreateAsync(
            new() { Name = "Child", Slug = "parent/child", ParentId = parent.Data!.Id, IsActive = true },
            actor, "t1");

        Assert.True(child.Succeeded);
        Assert.Equal(parent.Data!.Id, child.Data!.ParentId);
    }

    [Fact]
    public async Task Create_ActiveChildUnderInactiveParent_ReturnsValidationError()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        var parent = await svc.CreateAsync(new() { Name = "Inactive Parent", Slug = "inactive-parent", IsActive = false }, actor, "t1");
        Assert.True(parent.Succeeded);

        var child = await svc.CreateAsync(
            new() { Name = "Active Child", Slug = "inactive-parent/child", ParentId = parent.Data!.Id, IsActive = true },
            actor, "t1");

        Assert.False(child.Succeeded);
        Assert.Contains(child.Errors, e => e.Field == "parentId");
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ChangeNameOnly_Succeeds()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        var created = await svc.CreateAsync(new() { Name = "Original", Slug = "original-cat", IsActive = true }, actor, "t1");
        Assert.True(created.Succeeded);

        var updated = await svc.UpdateAsync(
            created.Data!.Id,
            new() { Name = "Renamed", Slug = "original-cat", IsActive = true },
            actor, "t1");

        Assert.True(updated.Succeeded);
        Assert.Equal("Renamed", updated.Data!.Name);
    }

    [Fact]
    public async Task Update_CycleDetection_ReturnsValidationError()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        var parent = await svc.CreateAsync(new() { Name = "P", Slug = "p", IsActive = true }, actor, "t1");
        var child = await svc.CreateAsync(
            new() { Name = "C", Slug = "p/c", ParentId = parent.Data!.Id, IsActive = true }, actor, "t1");

        // Tentar fazer parent filho de seu próprio filho (ciclo)
        var cycled = await svc.UpdateAsync(
            parent.Data!.Id,
            new() { Name = "P", Slug = "p", ParentId = child.Data!.Id, IsActive = true },
            actor, "t1");

        Assert.False(cycled.Succeeded);
        Assert.Contains(cycled.Errors, e => e.Field == "parentId");
    }

    [Fact]
    public async Task Update_NonExistentCategory_ReturnsFailure()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.UpdateAsync(
            Guid.NewGuid(),
            new() { Name = "X", Slug = "x", IsActive = true },
            Guid.NewGuid(), "t1");

        Assert.False(result.Succeeded);
    }

    // ── Deactivate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deactivate_WithActiveChildren_ReturnsValidationError()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        var parent = await svc.CreateAsync(new() { Name = "P2", Slug = "p2", IsActive = true }, actor, "t1");
        await svc.CreateAsync(new() { Name = "C2", Slug = "p2/c2", ParentId = parent.Data!.Id, IsActive = true }, actor, "t1");

        var result = await svc.DeactivateAsync(parent.Data!.Id, actor, "t1");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == "children");
    }

    [Fact]
    public async Task Deactivate_LeafCategory_Succeeds()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        var cat = await svc.CreateAsync(new() { Name = "Leaf", Slug = "leaf-cat", IsActive = true }, actor, "t1");
        var deact = await svc.DeactivateAsync(cat.Data!.Id, actor, "t1");

        Assert.True(deact.Succeeded);
        var persisted = await db.Categories.FindAsync(cat.Data!.Id);
        Assert.False(persisted!.IsActive);
    }

    [Fact]
    public async Task Deactivate_AlreadyInactive_ReturnsSuccessFalse()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        var cat = await svc.CreateAsync(new() { Name = "Inactive", Slug = "inactive-cat", IsActive = false }, actor, "t1");
        var result = await svc.DeactivateAsync(cat.Data!.Id, actor, "t1");

        Assert.True(result.Succeeded);
        Assert.False(result.Data); // já estava inativa, retorna false
    }

    // ── Tree / Cache ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Tree_IsServedFromCache_OnSecondCall()
    {
        await using var db = CreateDb();
        var cache = CreateCache();
        var svc = CreateService(db, cache);
        var actor = Guid.NewGuid();

        await svc.CreateAsync(new() { Name = "Root", Slug = "root", IsActive = true }, actor, "t1");

        var first = await svc.TreeAsync();
        Assert.True(first.Succeeded);
        Assert.Single(first.Data!);

        // Adiciona diretamente ao DB sem passar pelo serviço
        db.Categories.Add(new Category { Name = "Sneaky", Slug = "sneaky", IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        // Segunda chamada deve retornar do cache (ainda 1 item)
        var second = await svc.TreeAsync();
        Assert.True(second.Succeeded);
        Assert.Single(second.Data!); // cache hit — "Sneaky" não aparece
    }

    [Fact]
    public async Task Tree_IsInvalidated_AfterCreate()
    {
        await using var db = CreateDb();
        var cache = CreateCache();
        var svc = CreateService(db, cache);
        var actor = Guid.NewGuid();

        await svc.CreateAsync(new() { Name = "Root", Slug = "rootx", IsActive = true }, actor, "t1");
        _ = await svc.TreeAsync(); // popula cache

        // Cria via serviço (invalida cache)
        await svc.CreateAsync(new() { Name = "Second", Slug = "second", IsActive = true }, actor, "t1");

        var tree = await svc.TreeAsync();
        Assert.Equal(2, tree.Data!.Count); // cache foi invalidado
    }

    [Fact]
    public async Task Tree_IsInvalidated_AfterDeactivate()
    {
        await using var db = CreateDb();
        var cache = CreateCache();
        var svc = CreateService(db, cache);
        var actor = Guid.NewGuid();

        var cat = await svc.CreateAsync(new() { Name = "ToDeact", Slug = "todeact", IsActive = true }, actor, "t1");
        _ = await svc.TreeAsync(); // popula cache

        await svc.DeactivateAsync(cat.Data!.Id, actor, "t1"); // invalida

        var tree = await svc.TreeAsync(); // rebusca do DB
        Assert.Single(tree.Data!);
        Assert.False(tree.Data!.First().IsActive);
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_NotFound_ReturnsFailure()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.GetByIdAsync(Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Field == "categoryId");
    }

    [Fact]
    public async Task GetById_WithParent_IncludesParentSlug()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        var parent = await svc.CreateAsync(new() { Name = "Parent", Slug = "parent-slug", IsActive = true }, actor, "t1");
        var child = await svc.CreateAsync(
            new() { Name = "Child", Slug = "parent-slug/child-slug", ParentId = parent.Data!.Id, IsActive = true },
            actor, "t1");

        var detail = await svc.GetByIdAsync(child.Data!.Id);
        Assert.True(detail.Succeeded);
        Assert.Equal("parent-slug", detail.Data!.ParentSlug);
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_SearchFiltersCorrectly()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        await svc.CreateAsync(new() { Name = "Eletrodomésticos", Slug = "eletrodomesticos", IsActive = true }, actor, "t1");
        await svc.CreateAsync(new() { Name = "Móveis", Slug = "moveis", IsActive = true }, actor, "t1");

        var result = await svc.ListAsync(0, 10, search: "eletro", isActive: null);

        Assert.True(result.Succeeded);
        Assert.Single(result.Data!.Items);
        Assert.Equal("Eletrodomésticos", result.Data!.Items[0].Name);
    }

    [Fact]
    public async Task List_FiltersByIsActive()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var actor = Guid.NewGuid();

        await svc.CreateAsync(new() { Name = "Active Cat", Slug = "active-cat", IsActive = true }, actor, "t1");
        await svc.CreateAsync(new() { Name = "Inactive Cat", Slug = "inactive-cat", IsActive = false }, actor, "t1");

        var active = await svc.ListAsync(0, 10, null, isActive: true);
        var inactive = await svc.ListAsync(0, 10, null, isActive: false);

        Assert.Single(active.Data!.Items);
        Assert.Single(inactive.Data!.Items);
        Assert.Equal("Active Cat", active.Data!.Items[0].Name);
        Assert.Equal("Inactive Cat", inactive.Data!.Items[0].Name);
    }

    [Fact]
    public async Task List_InvalidPagination_ReturnsValidationError()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.ListAsync(skip: -1, limit: 0, search: null, isActive: null);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }
}
