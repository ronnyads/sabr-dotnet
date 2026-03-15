using System.Net;
using System.Security.Claims;
using System.Text;
using Serilog;
using Serilog.Events;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Sabr.Application.Abstractions;
using Sabr.Application.Categories;
using Sabr.Application.Options;
using Sabr.Application.Services;
using Sabr.Api.Health;
using Sabr.Api.Middleware;
using Sabr.Api.Options;
using Sabr.Api.Security;
using Sabr.Api.Swagger;
using Sabr.Api.Tenant;
using Sabr.Domain.Protheus;
using Sabr.Infrastructure.Integrations.Mabang;
using Sabr.Infrastructure.Integrations.MercadoLivre;
using Sabr.Infrastructure.Integrations.TinyErp;
using Sabr.Infrastructure.Integrations.Shopify;
using Sabr.Infrastructure.Persistence;
using Sabr.Infrastructure.Persistence.Seeding;
using Sabr.Infrastructure.Storage;
using Sabr.Infrastructure.Services;
using Microsoft.Extensions.Caching.StackExchangeRedis;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, services, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("MachineName", Environment.MachineName)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"));
var trustedForwardedProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
var trustedForwardedNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];

builder.Services.AddHttpContextAccessor();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<ITenantProvider, HttpContextTenantProvider>();
builder.Services.AddScoped<ITenantResolver, TenantResolver>();
builder.Services.AddControllers();
builder.Services.AddMemoryCache();
builder.Services.AddHttpLogging(options =>
{
    // LGPD-safe: do not log request/response bodies or sensitive headers.
    options.LoggingFields =
        HttpLoggingFields.RequestMethod |
        HttpLoggingFields.RequestPath |
        HttpLoggingFields.ResponseStatusCode |
        HttpLoggingFields.Duration;
});

// Forwarded headers (when behind reverse proxy / load balancer)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.RequireHeaderSymmetry = true;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    Program.ConfigureTrustedForwarding(
        options,
        trustedForwardedProxies,
        trustedForwardedNetworks,
        builder.Environment.IsDevelopment());
});

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection("Database"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ProtheusOptions>()
    .Bind(builder.Configuration.GetSection("Protheus"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .PostConfigure(options =>
    {
        if (string.IsNullOrWhiteSpace(options.Key) && !string.IsNullOrWhiteSpace(options.Secret))
        {
            options.Key = options.Secret;
        }
    })
    .Validate(options => !string.IsNullOrWhiteSpace(options.Key) || !string.IsNullOrWhiteSpace(options.Secret),
        "JWT requires Jwt:Key or Jwt:Secret.")
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<BootstrapOptions>()
    .Bind(builder.Configuration.GetSection("Bootstrap"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RefreshTokenOptions>()
    .Bind(builder.Configuration.GetSection("RefreshToken"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<LoginProtectionOptions>()
    .Bind(builder.Configuration.GetSection(LoginProtectionOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<DocumentLookupOptions>()
    .Bind(builder.Configuration.GetSection("DocumentLookup"))
    .ValidateOnStart();

builder.Services.AddOptions<PublicCnpjOptions>()
    .Bind(builder.Configuration.GetSection("PublicCnpj"))
    .ValidateOnStart();

builder.Services.AddOptions<MercadoLivreOptions>()
    .Bind(builder.Configuration.GetSection(MercadoLivreOptions.SectionName))
    .PostConfigure(options =>
    {
        Program.MergeMercadoLivreCategoryMappings(builder.Configuration, options);
        var legacyMlApiKey = builder.Configuration.GetValue<string>("Ml:ApiKey")
            ?? builder.Configuration.GetValue<string>("Ml__ApiKey");
        if (string.IsNullOrWhiteSpace(options.Mabang.ApiKey) && !string.IsNullOrWhiteSpace(legacyMlApiKey))
        {
            options.Mabang.ApiKey = legacyMlApiKey;
        }
    })
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ProductVariantBackfillOptions>()
    .Bind(builder.Configuration.GetSection(ProductVariantBackfillOptions.SectionName))
    .ValidateOnStart();
var databaseOptions = builder.Configuration.GetSection("Database").Get<DatabaseOptions>();
var configuredConnectionString = builder.Configuration.GetConnectionString("Default");
var hasValidConfiguredConnectionString =
    !string.IsNullOrWhiteSpace(configuredConnectionString) &&
    !Program.LooksLikePlaceholder(configuredConnectionString);
var connectionString = hasValidConfiguredConnectionString
    ? configuredConnectionString!
    : Program.IsUsableDatabaseOptions(databaseOptions)
        ? databaseOptions!.BuildConnectionString()
        : throw new InvalidOperationException(
            "Database connection is not configured. Set ConnectionStrings__Default or Database options via environment/secrets.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

// -- Cache (Redis em producao, Memory em dev) ----------------------------
var redisConnStr = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnStr) && !redisConnStr.Contains("SET_VIA"))
{
    builder.Services.AddStackExchangeRedisCache(opts => { opts.Configuration = redisConnStr; opts.InstanceName = "sabr:"; });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}
builder.Services.AddScoped<ICacheService, DistributedCacheService>();

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<DatabaseReadinessHealthCheck>("database", tags: new[] { "ready" })
    .AddCheck<MercadoLivreHealthCheck>("mercadolivre", failureStatus: HealthStatus.Degraded, tags: new[] { "ready", "external" });

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<StorageOptions>>().Value);
builder.Services.AddScoped<IFileStorage, LocalFileStorage>();

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>();
if (jwtOptions is not null && string.IsNullOrWhiteSpace(jwtOptions.Key) && !string.IsNullOrWhiteSpace(jwtOptions.Secret))
{
    jwtOptions.Key = jwtOptions.Secret;
}
if (jwtOptions == null || string.IsNullOrWhiteSpace(jwtOptions.Key))
{
    throw new InvalidOperationException("JWT configuration is missing. Set Jwt:Key or Jwt:Secret.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            RoleClaimType = ClaimTypes.Role,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();
var loginProtectionOptions =
    builder.Configuration.GetSection(LoginProtectionOptions.SectionName).Get<LoginProtectionOptions>()
    ?? new LoginProtectionOptions();
var ipRateLimit = loginProtectionOptions.IpRateLimit;
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", context =>
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!ipRateLimit.Enabled)
        {
            return RateLimitPartition.GetNoLimiter(remoteIp);
        }

        return RateLimitPartition.GetFixedWindowLimiter(remoteIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, ipRateLimit.PermitLimit),
            Window = TimeSpan.FromSeconds(Math.Max(1, ipRateLimit.WindowSeconds)),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = Math.Max(0, ipRateLimit.QueueLimit)
        });
    });
    options.AddPolicy("document-lookup", context =>
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"doc:{remoteIp}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;

        int? retryAfterSeconds = null;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            response.Headers["Retry-After"] = retryAfterSeconds.Value.ToString();
        }

        var isDocumentLookup = context.HttpContext.Request.Path.StartsWithSegments("/api/v1/utils/doc", StringComparison.OrdinalIgnoreCase);
        await response.WriteAsJsonAsync(new
        {
            error = isDocumentLookup
                ? "Too many document lookups. Try again later."
                : "Too many login attempts. Try again later.",
            retryAfterSeconds,
            source = isDocumentLookup ? "document-lookup-rate-limit" : "ip-rate-limit"
        }, cancellationToken);
    };
});

var corsDomain = builder.Configuration.GetValue<string>("Cors:AllowedDomain") ?? "sabr.com";
var devOrigin = builder.Configuration.GetValue<string>("Cors:DevOrigin") ?? "http://localhost:4200";
var configuredAllowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var configuredOrigin in builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
{
    if (!string.IsNullOrWhiteSpace(configuredOrigin))
    {
        configuredAllowedOrigins.Add(configuredOrigin.Trim());
    }
}

foreach (var configuredOrigin in Program.ParseAllowedOrigins(builder.Configuration.GetValue<string>("AllowedOrigins")))
{
    configuredAllowedOrigins.Add(configuredOrigin);
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("Spa", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                return false;
            }

            if (string.Equals(origin, devOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (configuredAllowedOrigins.Contains(origin))
            {
                return true;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Host.EndsWith($".{corsDomain}", StringComparison.OrdinalIgnoreCase);
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
    });
});

builder.Services.AddScoped<ClientService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<PlatformUserService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<MercadoLivreOAuthService>();
builder.Services.AddScoped<MercadoLivreMappingService>();
builder.Services.AddScoped<MercadoLivreIntegrationService>();
builder.Services.AddScoped<MercadoLivreSyncService>();
builder.Services.AddScoped<MercadoLivreWebhookService>();
builder.Services.AddScoped<MercadoLivrePublishValidationService>();
builder.Services.AddScoped<MercadoLivrePublishService>();
// ListingDraftService registrado como concreto + todas as interfaces segregadas (ISP).
// Controllers/consumers que precisam de apenas uma responsabilidade injetam a interface minimal.
builder.Services.AddScoped<ListingDraftService>();
builder.Services.AddScoped<IListingDraftCrudService>(sp => sp.GetRequiredService<ListingDraftService>());
builder.Services.AddScoped<IListingFeeService>(sp => sp.GetRequiredService<ListingDraftService>());
builder.Services.AddScoped<IListingCategoryService>(sp => sp.GetRequiredService<ListingDraftService>());
builder.Services.AddScoped<IListingPublishService>(sp => sp.GetRequiredService<ListingDraftService>());
builder.Services.AddScoped<IListingQueryService>(sp => sp.GetRequiredService<ListingDraftService>());
builder.Services.AddScoped<MarketplaceCategoryResolver>();

builder.Services.AddScoped<MarketplaceAuditLogService>();
builder.Services.AddScoped<MarketplaceMabangDispatchService>();
builder.Services.AddScoped<StockAvailabilityService>();
builder.Services.AddScoped<MarketplaceOrderPaymentService>();
builder.Services.AddScoped<MarketplaceShipmentLabelService>();
builder.Services.AddScoped<OrderCancellationService>();
builder.Services.AddScoped<OrderFulfillmentService>();
builder.Services.AddScoped<ClientStoreService>();
builder.Services.AddScoped<WalletService>();
builder.Services.AddScoped<CatalogAuthorizationService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<CatalogSnapshotService>();
builder.Services.AddScoped<PriceCalculator>();
builder.Services.AddScoped<MyProductsService>();
builder.Services.AddScoped<ProductAdminService>();
builder.Services.AddScoped<ProductImagesService>();
builder.Services.AddScoped<ProductVariantService>();
builder.Services.AddScoped<ProductVariantBackfillService>();
builder.Services.AddScoped<AdminCategoryService>();
builder.Services.AddScoped<AdminCatalogService>();
builder.Services.AddScoped<AdminPlanService>();
builder.Services.AddScoped<AdminClientPlanSubscriptionService>();
builder.Services.AddScoped<StagingFakeSeeder>();
builder.Services.AddScoped<DevInitialSeeder>();
builder.Services.AddSingleton<LoginAttemptService>();
builder.Services.AddSingleton<MercadoLivreOAuthStateService>();
builder.Services.AddScoped<IProtheusOutboxProcessor, MockProtheusOutboxProcessor>();
// Workers are disabled in the API by default — they run in Sabr.Worker.
// Set BackgroundWorkers:Enabled=true in appsettings to run both in a single process (dev only).
if (builder.Configuration.GetValue("BackgroundWorkers:Enabled", false))
{
    builder.Services.AddHostedService<ProtheusOutboxWorker>();
    builder.Services.AddHostedService<MarketplaceSyncWorker>();
    builder.Services.AddHostedService<MarketplaceWebhookWorker>();
    builder.Services.AddHostedService<MarketplaceMabangWorker>();
    builder.Services.AddHostedService<MarketplaceReconcileNightlyWorker>();
    builder.Services.AddHostedService<ProductVariantBackfillWorker>();
}

// Forçar lookup real (BrasilAPI) para CNPJ; sem mock.
builder.Services.AddHttpClient("PublicCnpj", (sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<PublicCnpjOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
});
builder.Services.AddScoped<IDocumentLookup, PublicCnpjLookupService>();

builder.Services.AddHttpClient<ICepLookup, ViaCepLookupService>(client =>
{
    client.BaseAddress = new Uri("https://viacep.com.br/");
    client.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddHttpClient("Protheus", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<ProtheusOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

builder.Services.AddHttpClient<IMercadoLivreApiClient, MercadoLivreApiClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<MercadoLivreOptions>>().Value;
    client.BaseAddress = new Uri(options.ApiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<IMabangApiClient, MabangApiClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<MercadoLivreOptions>>().Value;
    var baseUrl = string.IsNullOrWhiteSpace(options.Mabang.BaseUrl)
        ? "http://localhost:8080"
        : options.Mabang.BaseUrl;
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.Mabang.TimeoutSeconds));
});

// ── Tiny ERP ─────────────────────────────────────────────────────────────────
builder.Services.Configure<TinyErpOptions>(builder.Configuration.GetSection("TinyErp"));
builder.Services.AddHttpClient<ITinyErpApiClient, TinyErpApiClient>();
builder.Services.AddScoped<TinyOAuthService>();
builder.Services.AddScoped<TinyIntegrationService>();

// ── Shopify ───────────────────────────────────────────────────────────────────
builder.Services.Configure<ShopifyOptions>(builder.Configuration.GetSection(ShopifyOptions.SectionName));
builder.Services.AddHttpClient<IShopifyApiClient, ShopifyApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<ShopifyOAuthStateService>();
builder.Services.AddScoped<ShopifyOAuthService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SABR 3.0",
        Version = "v1"
    });

    options.TagActionsBy(api =>
    {
        var controllerName = (api.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;
        return controllerName switch
        {
            "AdminPlatformUsers" => new[] { "AdminSystemUsers" },
            "AdminUsers" => new[] { "AdminTenantUsers" },
            _ => new[] { controllerName ?? "SABR 3.0" }
        };
    });

    options.DocumentFilter<RoleContextDocumentFilter>();
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "SABR 3.0 v1");
        options.DocumentTitle = "SABR 3.0";
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseForwardedHeaders();
app.UseSerilogRequestLogging(opts =>
{
    opts.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    opts.EnrichDiagnosticContext = (diag, ctx) =>
    {
        diag.Set("RemoteIp", ctx.Connection.RemoteIpAddress);
        diag.Set("TenantId", ctx.Items["sabr_tenant"] ?? "(none)");
    };
});
app.UseHttpLogging();

app.UseCors("Spa");

app.UseRouting();

var storageOptions = app.Services.GetRequiredService<StorageOptions>();
var productImagesDirectory = Path.Combine(storageOptions.GetBasePath(), "product-images");
Directory.CreateDirectory(productImagesDirectory);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(productImagesDirectory),
    RequestPath = "/product-images"
});

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<CsrfMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<AdminTenantPathMiddleware>();
app.UseMiddleware<TenantGuardMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
    ResponseWriter = Program.WriteHealthCheckResponseAsync
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = Program.WriteHealthCheckResponseAsync
});
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = Program.WriteHealthCheckResponseAsync
});

var system = app.MapGroup("/").WithTags("SABR 3.0");

system.MapGet("/", () => new { app = "SABR 3.0", version = "0.1.0", status = "running" });
system.MapGet("/tenant", (ITenantProvider tenant) => new { tenant = tenant.TenantId ?? "(not-set)" });

system.MapGet("/protheus/tag-example", () => new
{
    clientUpdate = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.UPDATE),
    internalRhCreate = ProtheusTag.Build(ProtheusPrefixes.InternalUserRh, ProtheusOperationType.CREATE),
    internalPurchasingCreate = ProtheusTag.Build(ProtheusPrefixes.InternalUserPurchasing, ProtheusOperationType.CREATE)
});

var seedStagingFake = args.Any(arg => string.Equals(arg, "--seed-staging-fake", StringComparison.OrdinalIgnoreCase));
var seedDevInitial = args.Any(arg => string.Equals(arg, "--seed-dev-initial", StringComparison.OrdinalIgnoreCase));
if (seedStagingFake && seedDevInitial)
{
    throw new InvalidOperationException("Use only one seed flag at a time: --seed-staging-fake or --seed-dev-initial.");
}

if (seedDevInitial)
{
    if (!app.Environment.IsDevelopment() && !app.Environment.IsStaging())
    {
        throw new InvalidOperationException("--seed-dev-initial is allowed only in Development/Staging.");
    }

    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
    var seeder = scope.ServiceProvider.GetRequiredService<DevInitialSeeder>();
    await seeder.SeedAsync();
    Console.WriteLine("Dev initial seed completed successfully.");
    return;
}

if (seedStagingFake)
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
    var seeder = scope.ServiceProvider.GetRequiredService<StagingFakeSeeder>();
    await seeder.SeedAsync();
    Console.WriteLine("Staging fake seed completed successfully.");
    return;
}
if (!seedStagingFake)
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (dbContext.Database.IsRelational())
    {
        var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync()).ToArray();
        if (pendingMigrations.Length > 0)
        {
            var logger = scope.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("StartupMigrationGuard");
            logger.LogWarning(
                "Pending database migrations detected. Applying on startup. Pending: {PendingMigrations}",
                pendingMigrations);

            await dbContext.Database.MigrateAsync();
            logger.LogInformation("Database migrations applied successfully during startup.");
        }
    }
}

ValidateMercadoLivreConfiguration(app);

app.Run();

public partial class Program
{
    private static bool IsUsableDatabaseOptions(DatabaseOptions? options)
    {
        return options is not null
               && !LooksLikePlaceholder(options.Host)
               && !LooksLikePlaceholder(options.Name)
               && !LooksLikePlaceholder(options.User)
               && !LooksLikePlaceholder(options.Password);
    }

    private static IEnumerable<string> ParseAllowedOrigins(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return Array.Empty<string>();
        }

        return rawValue
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value));
    }

    private static void ConfigureTrustedForwarding(
        ForwardedHeadersOptions options,
        IEnumerable<string> trustedProxies,
        IEnumerable<string> trustedNetworks,
        bool isDevelopment)
    {
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);

        foreach (var value in trustedProxies)
        {
            if (!IPAddress.TryParse(value, out var ip))
            {
                continue;
            }

            options.KnownProxies.Add(ip);
        }

        foreach (var value in trustedNetworks)
        {
            if (!TryParseNetwork(value, out var network))
            {
                continue;
            }

            options.KnownNetworks.Add(network);
        }

        if (!isDevelopment)
        {
            return;
        }

        // Dev fallback to keep proxy simulation working locally.
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
    }

    private static bool TryParseNetwork(string value, out Microsoft.AspNetCore.HttpOverrides.IPNetwork network)
    {
        network = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Any, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var ip) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        if (prefixLength < 0 || prefixLength > (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
        {
            return false;
        }

        network = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(ip, prefixLength);
        return true;
    }

    private static Task WriteHealthCheckResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = (int)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = (int)entry.Value.Duration.TotalMilliseconds,
                description = entry.Value.Description
            })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }

    private static void ValidateMercadoLivreConfiguration(WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<MercadoLivreOptions>>().Value;
        var issues = GetMercadoLivreConfigurationIssues(options);
        if (issues.Count == 0)
        {
            return;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MercadoLivreConfigGuard");
        var joinedIssues = string.Join("; ", issues);
        var guidance =
            "Configure MercadoLivre options using dotnet user-secrets or environment variables: " +
            "MercadoLivre__ClientId, MercadoLivre__ClientSecret, MercadoLivre__RedirectUri, MercadoLivre__WebhookSecret.";

        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException($"Invalid MercadoLivre configuration: {joinedIssues}. {guidance}");
        }

        logger.LogWarning(
            "MercadoLivre configuration has placeholder values in DEV. {Issues}. {Guidance}",
            joinedIssues,
            guidance);
    }

    private static List<string> GetMercadoLivreConfigurationIssues(MercadoLivreOptions options)
    {
        var issues = new List<string>();

        if (LooksLikePlaceholder(options.ClientId))
        {
            issues.Add("ClientId");
        }

        if (LooksLikePlaceholder(options.ClientSecret))
        {
            issues.Add("ClientSecret");
        }

        if (LooksLikePlaceholder(options.RedirectUri))
        {
            issues.Add("RedirectUri");
        }

        if (options.Features.Webhook && LooksLikePlaceholder(options.WebhookSecret))
        {
            issues.Add("WebhookSecret");
        }

        return issues;
    }

    private static void MergeMercadoLivreCategoryMappings(IConfiguration configuration, MercadoLivreOptions options)
    {
        var mergedMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.CategoryMappings is { Count: > 0 })
        {
            foreach (var pair in options.CategoryMappings)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                mergedMappings[pair.Key.Trim()] = pair.Value.Trim();
            }
        }

        var mappingsSection = configuration.GetSection($"{MercadoLivreOptions.SectionName}:CategoryMappings");
        if (mappingsSection.Exists())
        {
            foreach (var child in mappingsSection.GetChildren())
            {
                MergeCategoryMappingSection(child, child.Key.Trim(), mergedMappings);
            }
        }

        options.CategoryMappings = mergedMappings;
    }

    private static void MergeCategoryMappingSection(
        IConfigurationSection section,
        string keyPrefix,
        IDictionary<string, string> target)
    {
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            target[keyPrefix] = section.Value.Trim();
        }

        foreach (var child in section.GetChildren())
        {
            var childKey = $"{keyPrefix}:{child.Key.Trim()}";
            MergeCategoryMappingSection(child, childKey, target);
        }
    }

    private static bool LooksLikePlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim();
        return normalized.StartsWith("__SET_VIA_", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("<", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(">", StringComparison.OrdinalIgnoreCase);
    }
}
