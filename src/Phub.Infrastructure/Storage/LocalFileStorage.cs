using Phub.Application.Abstractions;

namespace Phub.Infrastructure.Storage;

public sealed class LocalFileStorage : IFileStorage
{
    private readonly StorageOptions _options;

    public LocalFileStorage(StorageOptions options)
    {
        _options = options;
    }

    public async Task<FileStorageResult> SaveAsync(FileStorageRequest request, CancellationToken cancellationToken = default)
    {
        var basePath = _options.GetBasePath();
        var fullPath = Path.Combine(basePath, request.RelativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
        await request.Content.CopyToAsync(fileStream, cancellationToken);

        var url = _options.GetPublicUrl(request.RelativePath);
        var size = fileStream.Length;

        return new FileStorageResult(fullPath, url, size);
    }
}

public sealed class StorageOptions
{
    public string BasePath { get; set; } = "storage";
    public string? PublicBaseUrl { get; set; }

    public string GetBasePath()
    {
        if (Path.IsPathRooted(BasePath)) return BasePath;
        return Path.Combine(AppContext.BaseDirectory, BasePath);
    }

    public string GetPublicUrl(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace("\\", "/").Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "/";
        }

        if (string.IsNullOrWhiteSpace(PublicBaseUrl))
        {
            return $"/{normalized}";
        }

        return $"{PublicBaseUrl.TrimEnd('/')}/{normalized}";
    }
}
