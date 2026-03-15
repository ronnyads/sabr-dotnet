using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sabr.Api.Tests.TestHost;
using Sabr.Application.Models;
using Sabr.Application.Security;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.Protheus;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Api.Tests.Integration;

public sealed class AuthHttpTests : IClassFixture<TestWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TestWebApplicationFactory _factory;

    public AuthHttpTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_WithoutTenantHeader_ResolvesTenantByCredentials()
    {
        const string tenantId = "tenant-auth-001";
        const string tenantSlug = "auth001";
        const string email = "cliente.auth001@example.test";
        const string password = "Password123!";

        await _factory.ResetDatabaseAsync();
        await SeedClientAsync(tenantId, tenantSlug, email, password);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<LoginResponseDto>(JsonOptions);
        Assert.NotNull(payload);
        Assert.False(string.IsNullOrWhiteSpace(payload!.AccessToken));
        Assert.Equal("client", payload.AccountType);
        Assert.Equal(tenantId, payload.User.TenantId);
        Assert.Equal(tenantSlug, payload.User.TenantSlug);
    }

    [Fact]
    public async Task Login_WithoutTenantHeader_InvalidCredentials_ReturnsUnauthorized()
    {
        const string tenantId = "tenant-auth-002";
        const string tenantSlug = "auth002";
        const string email = "cliente.auth002@example.test";
        const string password = "Password123!";

        await _factory.ResetDatabaseAsync();
        await SeedClientAsync(tenantId, tenantSlug, email, password);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithoutTenantHeader_WhenAmbiguous_ReturnsConflict()
    {
        const string email = "duplicado.auth@example.test";
        const string password = "Password123!";

        await _factory.ResetDatabaseAsync();
        await SeedClientAsync("tenant-auth-003", "auth003", email, password);
        await SeedClientAsync("tenant-auth-004", "auth004", email, password);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var apiError = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(apiError);
        Assert.Equal("TENANT_AMBIGUOUS", apiError!.Code);
    }

    [Fact]
    public async Task Login_WithTenantHeader_StillWorks()
    {
        const string tenantId = "tenant-auth-005";
        const string tenantSlug = "auth005";
        const string email = "cliente.auth005@example.test";
        const string password = "Password123!";

        await _factory.ResetDatabaseAsync();
        await SeedClientAsync(tenantId, tenantSlug, email, password);

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<LoginResponseDto>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(tenantId, payload!.User.TenantId);
        Assert.Equal(tenantSlug, payload.User.TenantSlug);
    }

    [Fact]
    public async Task Csrf_WithoutTenantHeader_ReturnsOk()
    {
        await _factory.ResetDatabaseAsync();

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });

        var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SeedClientAsync(string tenantId, string tenantSlug, string email, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.Tenants.AnyAsync(item => item.Id == tenantId))
        {
            db.Tenants.Add(new Sabr.Domain.Entities.Tenant
            {
                Id = tenantId,
                Name = $"Tenant {tenantSlug}",
                Slug = tenantSlug,
                Status = TenantStatus.Active
            });
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (!await db.Clients.AnyAsync(item => item.TenantId == tenantId && item.Email == normalizedEmail))
        {
            db.Clients.Add(new Client
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProtheusCode = $"P-{tenantSlug.ToUpperInvariant()}",
                AccountName = $"Client {tenantSlug}",
                Email = normalizedEmail,
                PasswordHash = PasswordHasher.HashPassword(password),
                Status = ClientStatus.Approved,
                MustChangePassword = false,
                ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE)
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed class LoginResponseDto
    {
        public string AccessToken { get; set; } = string.Empty;
        public string AccountType { get; set; } = string.Empty;
        public LoginUserDto User { get; set; } = new();
    }

    private sealed class LoginUserDto
    {
        public string TenantId { get; set; } = string.Empty;
        public string? TenantSlug { get; set; }
    }
}
