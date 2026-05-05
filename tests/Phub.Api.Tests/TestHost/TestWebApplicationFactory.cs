using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Application.Abstractions;
using Phub.Infrastructure.Persistence;
using Phub.Infrastructure.Services;

namespace Phub.Api.Tests.TestHost;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"sabr-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            // Injeta a mesma chave usada por TestJwtFactory para que a API valide
            // tokens de teste sem mismatch de assinatura JWT (→ 401 falso).
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"]    = TestJwtFactory.TestSigningKey,
                ["Jwt:Secret"] = TestJwtFactory.TestSigningKey,
            });
        });
        builder.ConfigureServices(services =>
        {
            // Program.cs lê builder.Configuration antes do ConfigureAppConfiguration injetar
            // o override, portanto sobrescrevemos o IssuerSigningKey diretamente via PostConfigure.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, opts =>
            {
                opts.TokenValidationParameters.IssuerSigningKey =
                    new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(TestJwtFactory.TestSigningKey));
            });

            var hostedServices = services
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService) &&
                    descriptor.ImplementationType == typeof(ProductVariantBackfillWorker))
                .ToList();
            foreach (var descriptor in hostedServices)
            {
                services.Remove(descriptor);
            }

            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<IAppDbContext>();

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
            });
            services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        });
    }

    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        if (!await db.Categories.AnyAsync(item => item.Slug == ProductAdminService.UncategorizedSlug))
        {
            db.Categories.Add(new Category
            {
                Name = "Sem Categoria",
                Slug = ProductAdminService.UncategorizedSlug,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }
    }

    public HttpClient CreateTenantClient(string slug, string tenantId, Guid clientId, Guid? userId = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{slug}.local")
        });

        var token = TestJwtFactory.CreateTenantClientToken(tenantId, clientId, userId ?? clientId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient CreateAdminClient(Guid? userId = null, string role = "Admin")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://admin.local")
        });

        var token = TestJwtFactory.CreateAdminToken(userId ?? Guid.NewGuid(), role);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
