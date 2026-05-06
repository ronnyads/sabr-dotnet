using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phub.Application.Abstractions;
using Phub.Application.Categories;
using Phub.Application.Options;
using Phub.Application.Services;
using Phub.Domain.Protheus;
using Phub.Infrastructure.Integrations.Mabang;
using Phub.Infrastructure.Integrations.MercadoLivre;
using Phub.Infrastructure.Persistence;
using Phub.Infrastructure.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // ── Serilog ───────────────────────────────────────────────────────────────
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"));

    // ── Options ───────────────────────────────────────────────────────────────
    builder.Services.AddOptions<DatabaseOptions>()
        .Bind(builder.Configuration.GetSection("Database"))
        .ValidateDataAnnotations();

    builder.Services.AddOptions<ProtheusOptions>()
        .Bind(builder.Configuration.GetSection("Protheus"))
        .ValidateDataAnnotations();

    builder.Services.AddOptions<MercadoLivreOptions>()
        .Bind(builder.Configuration.GetSection(MercadoLivreOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddOptions<ProductVariantBackfillOptions>()
        .Bind(builder.Configuration.GetSection(ProductVariantBackfillOptions.SectionName))
        .ValidateOnStart();

    // ── Database ──────────────────────────────────────────────────────────────
    var configuredConnectionString = builder.Configuration.GetConnectionString("Default");
    var databaseOptions = builder.Configuration.GetSection("Database").Get<DatabaseOptions>();
    var hasValidConnStr =
        !string.IsNullOrWhiteSpace(configuredConnectionString) &&
        !configuredConnectionString.Contains("SET_VIA");

    var connectionString = hasValidConnStr
        ? configuredConnectionString!
        : IsUsableDatabaseOptions(databaseOptions)
            ? databaseOptions.BuildConnectionString()
            : throw new InvalidOperationException(
                "Database connection is not configured. Set ConnectionStrings__Default.");

    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString));

    // ── HttpClients ───────────────────────────────────────────────────────────
    builder.Services.AddHttpClient<IMercadoLivreApiClient, MercadoLivreApiClient>((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<MercadoLivreOptions>>().Value;
        client.BaseAddress = new Uri(opts.ApiBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
    });

    builder.Services.AddHttpClient<IMabangApiClient, MabangApiClient>((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<MercadoLivreOptions>>().Value;
        var baseUrl = string.IsNullOrWhiteSpace(opts.Mabang.BaseUrl)
            ? "http://localhost:8080"
            : opts.Mabang.BaseUrl;
        client.BaseAddress = new Uri(baseUrl);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, opts.Mabang.TimeoutSeconds));
    });

    builder.Services.AddHttpClient("Protheus", (sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<ProtheusOptions>>().Value;
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
    });

    // ── Application Services ──────────────────────────────────────────────────
    builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
    builder.Services.AddScoped<MercadoLivreSyncService>();
    builder.Services.AddScoped<MercadoLivreWebhookService>();
    builder.Services.AddScoped<MercadoLivreMappingService>();
    builder.Services.AddScoped<MercadoLivreOAuthService>();
    builder.Services.AddScoped<MercadoLivrePublishService>();
    builder.Services.AddScoped<MercadoLivrePublishValidationService>();
    builder.Services.AddScoped<MercadoLivreIntegrationService>();
    builder.Services.AddScoped<MarketplaceMabangDispatchService>();
    builder.Services.AddScoped<MarketplaceAuditLogService>();
    builder.Services.AddScoped<MarketplaceOrderNumberService>();
    builder.Services.AddScoped<MarketplaceOrderInventoryService>();
    builder.Services.AddScoped<MarketplaceOrderPaymentService>();
    builder.Services.AddScoped<MarketplaceShipmentLabelService>();
    builder.Services.AddScoped<StockAvailabilityService>();
    builder.Services.AddScoped<ProductVariantBackfillService>();
    builder.Services.AddScoped<MarketplaceCategoryResolver>();
    builder.Services.AddScoped<IProtheusOutboxProcessor, MockProtheusOutboxProcessor>();

    // ── Workers ───────────────────────────────────────────────────────────────
    if (builder.Configuration.GetValue("Workers:Enabled", true))
    {
        builder.Services.AddHostedService<ProtheusOutboxWorker>();
        builder.Services.AddHostedService<MarketplaceSyncWorker>();
        builder.Services.AddHostedService<MarketplaceWebhookWorker>();
        builder.Services.AddHostedService<MarketplaceMabangWorker>();
        builder.Services.AddHostedService<MarketplaceReconcileNightlyWorker>();
        builder.Services.AddHostedService<ProductVariantBackfillWorker>();
    }

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Phub.Worker terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

static bool IsUsableDatabaseOptions(DatabaseOptions? options)
{
    return options is not null
           && !string.IsNullOrWhiteSpace(options.Host)
           && !string.IsNullOrWhiteSpace(options.Name)
           && !string.IsNullOrWhiteSpace(options.User)
           && !string.IsNullOrWhiteSpace(options.Password);
}
