namespace Phub.Application.Options;

public sealed class ProductVariantBackfillOptions
{
    public const string SectionName = "ProductVariantBackfill";

    public bool Enabled { get; set; } = true;
    public bool RunOnStartup { get; set; } = true;
    public int IntervalMinutes { get; set; } = 10;
    public int BatchSize { get; set; } = 200;
}

