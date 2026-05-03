namespace Phub.Application.Abstractions;

public interface IFileStorage
{
    Task<FileStorageResult> SaveAsync(FileStorageRequest request, CancellationToken cancellationToken = default);
}

public sealed record FileStorageRequest(
    string RelativePath,
    Stream Content,
    string ContentType
);

public sealed record FileStorageResult(
    string FilePath,
    string FileUrl,
    long SizeBytes
);
