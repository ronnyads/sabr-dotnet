using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Sabr.Api.Tenant;
using DomainTenant = Sabr.Domain.Entities.Tenant;
using Sabr.Infrastructure.Persistence;

namespace Sabr.Api.Tests;

public sealed class TenantResolverTests
{
    private sealed class TestHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Sabr.Api.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = default!;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task ResolveAsync_TenantHost_ResolvesTenant()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid().ToString("N");
        db.Tenants.Add(new DomainTenant { Id = tenantId, Slug = "loja", Name = "Loja", Status = Sabr.Domain.Enums.TenantStatus.Active });
        await db.SaveChangesAsync();

        var env = new TestHostEnvironment { EnvironmentName = Environments.Production };
        var resolver = new TenantResolver(db, env);

        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("loja.sabr.com.br");

        var result = await resolver.ResolveAsync(ctx);

        Assert.NotNull(result);
        Assert.False(result!.IsPlatform);
        Assert.Equal("loja", result.Slug);
        Assert.Equal(tenantId, result.Id);
    }

    [Fact]
    public async Task ResolveAsync_AdminHost_IsPlatform()
    {
        await using var db = CreateDb();
        var env = new TestHostEnvironment { EnvironmentName = Environments.Production };
        var resolver = new TenantResolver(db, env);

        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("admin.sabr.com.br");

        var result = await resolver.ResolveAsync(ctx);

        Assert.NotNull(result);
        Assert.True(result!.IsPlatform);
        Assert.Equal(string.Empty, result.Id);
        Assert.Equal(string.Empty, result.Slug);
    }

    [Fact]
    public async Task ResolveAsync_ReservedSubdomain_ReturnsNull()
    {
        await using var db = CreateDb();
        var env = new TestHostEnvironment { EnvironmentName = Environments.Production };
        var resolver = new TenantResolver(db, env);

        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("api.sabr.com.br");

        var result = await resolver.ResolveAsync(ctx);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_PlatformReservedSubdomain_ReturnsNull()
    {
        await using var db = CreateDb();
        var env = new TestHostEnvironment { EnvironmentName = Environments.Production };
        var resolver = new TenantResolver(db, env);

        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("platform.sabr.com.br");

        var result = await resolver.ResolveAsync(ctx);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_Localhost_UsesXTenant_InDevelopment()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid().ToString("N");
        db.Tenants.Add(new DomainTenant { Id = tenantId, Slug = "loja", Name = "Loja", Status = Sabr.Domain.Enums.TenantStatus.Active });
        await db.SaveChangesAsync();

        var env = new TestHostEnvironment { EnvironmentName = Environments.Development };
        var resolver = new TenantResolver(db, env);

        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("localhost");
        ctx.Request.Headers["X-Tenant"] = "loja";

        var result = await resolver.ResolveAsync(ctx);

        Assert.NotNull(result);
        Assert.Equal(tenantId, result!.Id);
        Assert.Equal("loja", result.Slug);
    }

    [Fact]
    public async Task ResolveAsync_Localhost_UsesOriginFallback_InDevelopment()
    {
        await using var db = CreateDb();
        var tenantId = Guid.NewGuid().ToString("N");
        db.Tenants.Add(new DomainTenant { Id = tenantId, Slug = "loja", Name = "Loja", Status = Sabr.Domain.Enums.TenantStatus.Active });
        await db.SaveChangesAsync();

        var env = new TestHostEnvironment { EnvironmentName = Environments.Development };
        var resolver = new TenantResolver(db, env);

        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("localhost");
        ctx.Request.Headers["Origin"] = "http://loja.lvh.me:4200";

        var result = await resolver.ResolveAsync(ctx);

        Assert.NotNull(result);
        Assert.Equal(tenantId, result!.Id);
        Assert.Equal("loja", result.Slug);
    }
}
