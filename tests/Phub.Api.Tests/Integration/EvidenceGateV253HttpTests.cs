using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Phub.Api.Tests.TestHost;
using Phub.Application.Models;
using Phub.Domain.Entities;
using Phub.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace Phub.Api.Tests.Integration;

public sealed class EvidenceGateV253HttpTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public EvidenceGateV253HttpTests(TestWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task EvidencePack_MinimumSixCases_PrintsStatusAndApiErrorCode()
    {
        // Case 1: 422 CATEGORY_HAS_ACTIVE_CHILDREN
        await _factory.ResetDatabaseAsync();
        using (var client = _factory.CreateAdminClient())
        {
            var parentResponse = await client.PostAsJsonAsync("/api/v1/admin/categories", new AdminCategoryUpsertRequest
            {
                Name = "Eletronicos",
                Slug = "eletronicos",
                IsActive = true
            });
            parentResponse.EnsureSuccessStatusCode();
            var parent = await parentResponse.Content.ReadFromJsonAsync<AdminCategoryDetailResult>();
            Assert.NotNull(parent);

            var childResponse = await client.PostAsJsonAsync("/api/v1/admin/categories", new AdminCategoryUpsertRequest
            {
                Name = "Audio",
                Slug = "audio",
                ParentId = parent!.Id,
                IsActive = true
            });
            childResponse.EnsureSuccessStatusCode();

            var blocked = await client.DeleteAsync($"/api/v1/admin/categories/{parent.Id}");
            var blockedError = await blocked.Content.ReadFromJsonAsync<ApiError>();
            _output.WriteLine($"CASE1 status={(int)blocked.StatusCode} code={blockedError?.Code}");
            Console.WriteLine($"CASE1 status={(int)blocked.StatusCode} code={blockedError?.Code}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
            Assert.Equal("CATEGORY_HAS_ACTIVE_CHILDREN", blockedError?.Code);
        }

        // Case 2: 422 CATEGORY_CYCLE_DETECTED
        await _factory.ResetDatabaseAsync();
        using (var client = _factory.CreateAdminClient())
        {
            var rootResponse = await client.PostAsJsonAsync("/api/v1/admin/categories", new AdminCategoryUpsertRequest
            {
                Name = "Pai",
                Slug = "pai",
                IsActive = true
            });
            rootResponse.EnsureSuccessStatusCode();
            var root = await rootResponse.Content.ReadFromJsonAsync<AdminCategoryDetailResult>();
            Assert.NotNull(root);

            var childResponse = await client.PostAsJsonAsync("/api/v1/admin/categories", new AdminCategoryUpsertRequest
            {
                Name = "Filho",
                Slug = "filho",
                ParentId = root!.Id,
                IsActive = true
            });
            childResponse.EnsureSuccessStatusCode();
            var child = await childResponse.Content.ReadFromJsonAsync<AdminCategoryDetailResult>();
            Assert.NotNull(child);

            var cycle = await client.PutAsJsonAsync($"/api/v1/admin/categories/{root.Id}", new AdminCategoryUpsertRequest
            {
                Name = "Pai",
                Slug = "pai",
                ParentId = child!.Id,
                IsActive = true
            });
            var cycleError = await cycle.Content.ReadFromJsonAsync<ApiError>();
            _output.WriteLine($"CASE2 status={(int)cycle.StatusCode} code={cycleError?.Code}");
            Console.WriteLine($"CASE2 status={(int)cycle.StatusCode} code={cycleError?.Code}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, cycle.StatusCode);
            Assert.Equal("CATEGORY_CYCLE_DETECTED", cycleError?.Code);
        }

        // Cases 3-6: product category semantics on POST/PUT
        await _factory.ResetDatabaseAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Categories.Add(new Category
        {
            Name = "Hardware",
            Slug = "hardware",
            IsActive = true
        });
        db.Categories.Add(new Category
        {
            Name = "Inativa",
            Slug = "cat-inativa",
            IsActive = false
        });
        await db.SaveChangesAsync();

        using (var client = _factory.CreateAdminClient())
        {
            // Case 3: POST without category -> uncategorized
            var post = await client.PostAsJsonAsync("/api/v1/admin/products", new AdminProductUpsertRequest
            {
                Sku = "sku-proof-01",
                Name = "Produto sem categoria",
                Brand = "Marca Prova",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = false
            });
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            var postGet = await client.GetAsync("/api/v1/admin/products/SKU-PROOF-01");
            postGet.EnsureSuccessStatusCode();
            var postResult = await postGet.Content.ReadFromJsonAsync<AdminProductResult>();
            _output.WriteLine($"CASE3 status={(int)post.StatusCode} categoryId={postResult?.CategoryId}");
            Console.WriteLine($"CASE3 status={(int)post.StatusCode} categoryId={postResult?.CategoryId}");
            Assert.Equal("uncategorized", postResult?.CategoryId);

            // Prepare product with explicit category for PUT checks.
            var create = await client.PostAsJsonAsync("/api/v1/admin/products", new AdminProductUpsertRequest
            {
                Sku = "SKU-PROOF-02",
                Name = "Produto Categoria",
                Brand = "Marca Categoria",
                CategoryId = "hardware",
                CostPriceCents = 1000,
                CatalogPriceCents = 1500,
                IsActive = false
            });
            create.EnsureSuccessStatusCode();

            // Case 4: PUT without category field -> keep
            using (var putWithoutField = new HttpRequestMessage(HttpMethod.Put, "/api/v1/admin/products/SKU-PROOF-02")
            {
                Content = new StringContent("{\"name\":\"Produto Atualizado\"}", Encoding.UTF8, "application/json")
            })
            {
                var putKeep = await client.SendAsync(putWithoutField);
                var putKeepResult = await putKeep.Content.ReadFromJsonAsync<AdminProductResult>();
                _output.WriteLine($"CASE4 status={(int)putKeep.StatusCode} categoryId={putKeepResult?.CategoryId}");
                Console.WriteLine($"CASE4 status={(int)putKeep.StatusCode} categoryId={putKeepResult?.CategoryId}");
                Assert.Equal(HttpStatusCode.OK, putKeep.StatusCode);
                Assert.Equal("hardware", putKeepResult?.CategoryId);
            }

            // Case 5: PUT category not found -> 422 CATEGORY_NOT_FOUND
            var putNotFound = await client.PutAsJsonAsync("/api/v1/admin/products/SKU-PROOF-02", new AdminProductUpdateRequest
            {
                CategoryId = "does-not-exist"
            });
            var notFoundError = await putNotFound.Content.ReadFromJsonAsync<ApiError>();
            _output.WriteLine($"CASE5 status={(int)putNotFound.StatusCode} code={notFoundError?.Code}");
            Console.WriteLine($"CASE5 status={(int)putNotFound.StatusCode} code={notFoundError?.Code}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, putNotFound.StatusCode);
            Assert.Equal("CATEGORY_NOT_FOUND", notFoundError?.Code);

            // Case 6: PUT category inactive -> 422 CATEGORY_INACTIVE
            var putInactive = await client.PutAsJsonAsync("/api/v1/admin/products/SKU-PROOF-02", new AdminProductUpdateRequest
            {
                CategoryId = "cat-inativa"
            });
            var inactiveError = await putInactive.Content.ReadFromJsonAsync<ApiError>();
            _output.WriteLine($"CASE6 status={(int)putInactive.StatusCode} code={inactiveError?.Code}");
            Console.WriteLine($"CASE6 status={(int)putInactive.StatusCode} code={inactiveError?.Code}");
            Assert.Equal(HttpStatusCode.UnprocessableEntity, putInactive.StatusCode);
            Assert.Equal("CATEGORY_INACTIVE", inactiveError?.Code);
        }
    }
}
