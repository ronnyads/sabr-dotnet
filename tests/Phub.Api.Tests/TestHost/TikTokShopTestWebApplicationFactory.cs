using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
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
using Phub.Domain.Enums;
using Phub.Domain.Entities;
using Phub.Infrastructure.Persistence;
using Phub.Infrastructure.Services;

namespace Phub.Api.Tests.TestHost;

public sealed class TikTokShopTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"sabr-tiktok-tests-{Guid.NewGuid():N}";

    public FakeTikTokShopApiClient FakeTikTokShopApiClient { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestJwtFactory.TestSigningKey,
                ["Jwt:Secret"] = TestJwtFactory.TestSigningKey,
                ["Jwt:Issuer"] = "SABR3",
                ["Jwt:Audience"] = "SABR3",
                ["TikTokShop:AppKey"] = "DEV_TTS_APP_KEY",
                ["TikTokShop:AppSecret"] = "DEV_TTS_APP_SECRET",
                ["TikTokShop:RedirectUri"] = "http://localhost:5250/api/v1/client/integrations/tiktokshop/callback",
                ["TikTokShop:ClientPortalBaseUrl"] = "http://localhost:4200",
                ["TikTokShop:Features:Publish"] = "true"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, opts =>
            {
                opts.TokenValidationParameters.IssuerSigningKey =
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtFactory.TestSigningKey));
            });

            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<IAppDbContext>();
            services.RemoveAll<ITikTokShopApiClient>();

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
            services.AddSingleton<ITikTokShopApiClient>(FakeTikTokShopApiClient);
        });
    }

    public async Task ResetDatabaseAsync()
    {
        FakeTikTokShopApiClient.Reset();

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

        var token = CreateTenantClientToken(tenantId, clientId, userId ?? clientId);
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

    private static string CreateTenantClientToken(string tenantId, Guid clientId, Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtFactory.TestSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("tenantId", tenantId),
            new Claim("clientId", clientId.ToString()),
            new Claim("scope", "tenant"),
            new Claim("accountType", AccountTypes.Client)
        };

        var token = new JwtSecurityToken(
            issuer: "PHUB3",
            audience: "PHUB3",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
