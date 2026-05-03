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
using Phub.Application.Abstractions;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Infrastructure.Persistence;
using Phub.Infrastructure.Services;

namespace Phub.Api.Tests.TestHost;

public sealed class MercadoLivreTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"sabr-ml-tests-{Guid.NewGuid():N}";

    public FakeMercadoLivreApiClient FakeMercadoLivreApiClient { get; } = new();
    public FakeMabangApiClient FakeMabangApiClient { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // JWT: mesma chave que TestJwtFactory usa para assinar → evita mismatch (401 falso).
                ["Jwt:Key"]    = TestJwtFactory.TestSigningKey,
                ["Jwt:Secret"] = TestJwtFactory.TestSigningKey,
                ["MercadoLivre:ClientId"] = "DEV_ML_CLIENT_ID",
                ["MercadoLivre:ClientSecret"] = "DEV_ML_CLIENT_SECRET",
                ["MercadoLivre:RedirectUri"] = "http://localhost:5250/api/v1/client/integrations/mercadolivre/callback",
                ["MercadoLivre:WebhookSecret"] = "DEV_ML_WEBHOOK_SECRET",
                ["MercadoLivre:CategoryMappings:MLA:ml-test-slug"] = "MLA1055",
                ["MercadoLivre:CategoryMappings:ml-test-slug"] = "MLB1055",
                ["MercadoLivre:CategoryMappings:ml-invalid-slug"] = "MLB1055",
                ["MercadoLivre:CategoryMappings:tratamentos-de-beleza"] = "MLB277912",
                ["MercadoLivre:CategoryMappings:MLB:tratamentos-de-beleza"] = "MLB277912",
                ["MercadoLivre:CategoryMappings:geladeiras-termicas"] = "MLB18272",
                ["MercadoLivre:CategoryMappings:MLB:geladeiras-termicas"] = "MLB18272"
            });
        });
        builder.ConfigureServices(services =>
        {
            // Program.cs lê builder.Configuration antes do ConfigureAppConfiguration injetar
            // override, portanto sobrescrevemos o IssuerSigningKey via PostConfigure.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, opts =>
            {
                opts.TokenValidationParameters.IssuerSigningKey =
                    new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(TestJwtFactory.TestSigningKey));
            });

            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<IAppDbContext>();
            services.RemoveAll<IMercadoLivreApiClient>();
            services.RemoveAll<IMabangApiClient>();

            var hostedServices = services
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService) &&
                    (descriptor.ImplementationType == typeof(MarketplaceSyncWorker)
                     || descriptor.ImplementationType == typeof(MarketplaceWebhookWorker)
                     || descriptor.ImplementationType == typeof(MarketplaceMabangWorker)
                     || descriptor.ImplementationType == typeof(MarketplaceReconcileNightlyWorker)
                     || descriptor.ImplementationType == typeof(ProductVariantBackfillWorker)))
                .ToList();
            foreach (var descriptor in hostedServices)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
            });
            services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
            services.AddSingleton<IMercadoLivreApiClient>(FakeMercadoLivreApiClient);
            services.AddSingleton<IMabangApiClient>(FakeMabangApiClient);
        });
    }

    public async Task ResetDatabaseAsync()
    {
        FakeMercadoLivreApiClient.Reset();
        FakeMabangApiClient.Reset();

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

    public HttpClient CreateTenantClientWithoutRedirect(string slug, string tenantId, Guid clientId, Guid? userId = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{slug}.local"),
            AllowAutoRedirect = false
        });

        var token = TestJwtFactory.CreateTenantClientToken(tenantId, clientId, userId ?? clientId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient CreateAnonymousClientWithoutRedirect(string host = "http://localhost")
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(host),
            AllowAutoRedirect = false
        });
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
