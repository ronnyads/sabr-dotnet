using System.Net;
using System.Net.Http.Json;
using Sabr.Api.Tests.TestHost;
using Sabr.Application.Models;

namespace Sabr.Api.Tests.Integration;

public sealed class AdminCategoriesHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AdminCategoriesHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminCategories_DeactivateParentWithActiveChild_ReturnsCategoryHasActiveChildren()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateAdminClient();

        var parentResponse = await client.PostAsJsonAsync("/api/v1/admin/categories", new AdminCategoryUpsertRequest
        {
            Name = "Eletronicos",
            Slug = "eletronicos",
            IsActive = true
        });

        Assert.Equal(HttpStatusCode.Created, parentResponse.StatusCode);
        var parent = await parentResponse.Content.ReadFromJsonAsync<AdminCategoryDetailResult>();
        Assert.NotNull(parent);

        var childResponse = await client.PostAsJsonAsync("/api/v1/admin/categories", new AdminCategoryUpsertRequest
        {
            Name = "Audio",
            Slug = "audio",
            ParentId = parent!.Id,
            IsActive = true
        });

        Assert.Equal(HttpStatusCode.Created, childResponse.StatusCode);
        var child = await childResponse.Content.ReadFromJsonAsync<AdminCategoryDetailResult>();
        Assert.NotNull(child);

        var deactivateParentBlocked = await client.DeleteAsync($"/api/v1/admin/categories/{parent.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deactivateParentBlocked.StatusCode);
        var blockedError = await deactivateParentBlocked.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(blockedError);
        Assert.Equal("CATEGORY_HAS_ACTIVE_CHILDREN", blockedError!.Code);

        var deactivateChild = await client.DeleteAsync($"/api/v1/admin/categories/{child!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateChild.StatusCode);

        var deactivateParent = await client.DeleteAsync($"/api/v1/admin/categories/{parent.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateParent.StatusCode);

        var deactivateParentAgain = await client.DeleteAsync($"/api/v1/admin/categories/{parent.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deactivateParentAgain.StatusCode);
    }

    [Fact]
    public async Task AdminCategories_WhenUpdateCreatesCycle_ReturnsCategoryCycleDetected()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateAdminClient();

        var rootResponse = await client.PostAsJsonAsync("/api/v1/admin/categories", new AdminCategoryUpsertRequest
        {
            Name = "Pai",
            Slug = "pai",
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.Created, rootResponse.StatusCode);
        var root = await rootResponse.Content.ReadFromJsonAsync<AdminCategoryDetailResult>();
        Assert.NotNull(root);

        var childResponse = await client.PostAsJsonAsync("/api/v1/admin/categories", new AdminCategoryUpsertRequest
        {
            Name = "Filho",
            Slug = "filho",
            ParentId = root!.Id,
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.Created, childResponse.StatusCode);
        var child = await childResponse.Content.ReadFromJsonAsync<AdminCategoryDetailResult>();
        Assert.NotNull(child);

        var cycleResponse = await client.PutAsJsonAsync($"/api/v1/admin/categories/{root.Id}", new AdminCategoryUpsertRequest
        {
            Name = "Pai",
            Slug = "pai",
            ParentId = child!.Id,
            IsActive = true
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, cycleResponse.StatusCode);
        var cycleError = await cycleResponse.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(cycleError);
        Assert.Equal("CATEGORY_CYCLE_DETECTED", cycleError!.Code);
    }
}
