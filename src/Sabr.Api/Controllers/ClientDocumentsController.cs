using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sabr.Api.Models;
using Sabr.Application.Abstractions;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.Protheus;
using Sabr.Infrastructure.Storage;

namespace Sabr.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/client-documents/{clientId:guid}")]
public sealed class ClientDocumentsController : ControllerBase
{
    private const long MaxFileSize = 10 * 1024 * 1024;
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;
    private static readonly DocumentType[] RequiredDocumentTypes =
    {
        DocumentType.CnpjCertificate,
        DocumentType.SocialContract,
        DocumentType.AddressProof,
        DocumentType.ResponsibleDocument
    };

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/jpg",
        "image/png"
    };

    private readonly IAppDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly StorageOptions _storageOptions;

    public ClientDocumentsController(IAppDbContext dbContext, IFileStorage fileStorage, StorageOptions storageOptions)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
        _storageOptions = storageOptions;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        Guid clientId,
        [FromQuery] int skip = 0,
        [FromQuery] int limit = DefaultPageSize,
        [FromQuery] DocumentStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var accountType = User.FindFirst("accountType")?.Value;
        if (string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase) && !IsClientMatch(clientId))
        {
            return Forbid();
        }

        if (skip < 0)
        {
            return BadRequest(new { errors = new[] { new { field = "skip", message = "Skip must be 0 or greater" } } });
        }

        if (limit <= 0 || limit > MaxPageSize)
        {
            return BadRequest(new { errors = new[] { new { field = "limit", message = $"Limit must be between 1 and {MaxPageSize}" } } });
        }

        var clientExists = await _dbContext.Clients.AnyAsync(s => s.Id == clientId, cancellationToken);
        if (!clientExists)
        {
            return NotFound(new { error = "Client not found" });
        }

        var query = _dbContext.ClientDocuments.Where(d => d.ClientId == clientId);
        if (status.HasValue)
        {
            query = query.Where(d => d.Status == status.Value);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(d => d.SubmittedAt)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return Ok(new ClientDocumentListResponse
        {
            Items = items.Select(MapToResult).ToList(),
            Total = total,
            Skip = skip,
            Limit = limit
        });
    }

    [HttpGet("{documentId:guid}")]
    public async Task<IActionResult> Get(Guid clientId, Guid documentId, CancellationToken cancellationToken)
    {
        var accountType = User.FindFirst("accountType")?.Value;
        if (string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase) && !IsClientMatch(clientId))
        {
            return Forbid();
        }

        var document = await _dbContext.ClientDocuments
            .FirstOrDefaultAsync(d => d.ClientId == clientId && d.Id == documentId, cancellationToken);

        if (document == null)
        {
            return NotFound(new { error = "Document not found" });
        }

        return Ok(MapToResult(document));
    }

    [HttpGet("{documentId:guid}/download")]
    public async Task<IActionResult> Download(Guid clientId, Guid documentId, CancellationToken cancellationToken)
    {
        var accountType = User.FindFirst("accountType")?.Value;
        if (string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase) && !IsClientMatch(clientId))
        {
            return Forbid();
        }

        var document = await _dbContext.ClientDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.ClientId == clientId && d.Id == documentId, cancellationToken);

        if (document == null)
        {
            return NotFound(new { error = "Document not found" });
        }

        if (string.IsNullOrWhiteSpace(document.FileUrl))
        {
            return NotFound(new { error = "Document file not found" });
        }

        // Extrai o path relativo independente de como foi salvo (URL absoluta ou path relativo)
        string normalizedRelativePath;
        if (Uri.TryCreate(document.FileUrl, UriKind.Absolute, out var absoluteFileUrl))
        {
            // Usa apenas o path da URL (descarta host/scheme)
            normalizedRelativePath = absoluteFileUrl.AbsolutePath.TrimStart('/');
        }
        else
        {
            normalizedRelativePath = document.FileUrl.Replace('\\', '/').TrimStart('/');
        }

        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            return NotFound(new { error = "Document file not found" });
        }

        var fullPath = Path.Combine(_storageOptions.GetBasePath(), normalizedRelativePath);
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound(new { error = "Document file not found" });
        }

        var contentType = string.IsNullOrWhiteSpace(document.ContentType)
            ? "application/octet-stream"
            : document.ContentType;
        var fileName = string.IsNullOrWhiteSpace(document.FileName)
            ? $"document-{document.Id}"
            : document.FileName;

        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return File(stream, contentType, fileName, enableRangeProcessing: true);
    }

    [HttpPost]
    [RequestSizeLimit(MaxFileSize)]
    public async Task<IActionResult> Upload(Guid clientId, [FromForm] ClientDocumentUploadRequest request, CancellationToken cancellationToken)
    {
        var accountType = User.FindFirst("accountType")?.Value;
        if (string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase) && !IsClientMatch(clientId))
        {
            return Forbid();
        }

        var client = await _dbContext.Clients.FirstOrDefaultAsync(s => s.Id == clientId, cancellationToken);
        if (client == null)
        {
            return NotFound(new { error = "Client not found" });
        }

        if (request.File == null || request.File.Length == 0)
        {
            return BadRequest(new { error = "File is required" });
        }

        if (request.File.Length > MaxFileSize)
        {
            return BadRequest(new { error = "File size exceeds 10MB" });
        }

        if (!AllowedContentTypes.Contains(request.File.ContentType))
        {
            return BadRequest(new { error = "Invalid file type" });
        }

        var safeName = Path.GetFileName(request.File.FileName);
        var relativePath = Path.Combine("client-documents", clientId.ToString(), $"{Guid.NewGuid()}_{safeName}");

        await using var stream = request.File.OpenReadStream();
        var stored = await _fileStorage.SaveAsync(new FileStorageRequest(relativePath, stream, request.File.ContentType), cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var tag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.UPDATE);
        var db = _dbContext as DbContext;
        if (db == null)
        {
            throw new InvalidOperationException("Database context is unavailable for document upsert.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO client_documents (
    ""Id"",
    ""ClientId"",
    ""DocumentType"",
    ""Status"",
    ""FileName"",
    ""ContentType"",
    ""SizeBytes"",
    ""FileUrl"",
    ""SubmittedAt"",
    ""TenantId"",
    ""ProtheusTag"",
    ""ProtheusOperation"",
    ""CreatedAt"",
    ""UpdatedAt"",
    ""RequestedAt"",
    ""ReviewReason"",
    ""ReviewedAt"",
    ""ReviewedByUserId""
)
VALUES (
    {Guid.NewGuid()},
    {clientId},
    {(int)request.DocumentType},
    {(int)DocumentStatus.Pending},
    {safeName},
    {request.File.ContentType},
    {stored.SizeBytes},
    {stored.FileUrl},
    {now},
    {client.TenantId},
    {tag},
    {(int)ProtheusOperationType.UPDATE},
    {now},
    {now},
    {(DateTimeOffset?)null},
    {(string?)null},
    {(DateTimeOffset?)null},
    {(Guid?)null}
)
ON CONFLICT (""ClientId"", ""DocumentType"")
DO UPDATE SET
    ""Status"" = EXCLUDED.""Status"",
    ""FileName"" = EXCLUDED.""FileName"",
    ""ContentType"" = EXCLUDED.""ContentType"",
    ""SizeBytes"" = EXCLUDED.""SizeBytes"",
    ""FileUrl"" = EXCLUDED.""FileUrl"",
    ""SubmittedAt"" = EXCLUDED.""SubmittedAt"",
    ""TenantId"" = EXCLUDED.""TenantId"",
    ""ProtheusTag"" = EXCLUDED.""ProtheusTag"",
    ""ProtheusOperation"" = EXCLUDED.""ProtheusOperation"",
    ""UpdatedAt"" = EXCLUDED.""UpdatedAt"",
    ""RequestedAt"" = NULL,
    ""ReviewReason"" = NULL,
    ""ReviewedAt"" = NULL,
    ""ReviewedByUserId"" = NULL;", cancellationToken);

        client.Status = ClientStatus.PendingDocuments;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var document = await _dbContext.ClientDocuments
            .AsNoTracking()
            .FirstAsync(d => d.ClientId == clientId && d.DocumentType == request.DocumentType, cancellationToken);

        return Ok(new { documentId = document.Id, status = document.Status });
    }

    [HttpPost("{documentId:guid}/request")]
    public async Task<IActionResult> RequestReview(Guid clientId, Guid documentId, CancellationToken cancellationToken)
    {
        var accountType = User.FindFirst("accountType")?.Value;
        if (string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase) && !IsClientMatch(clientId))
        {
            return Forbid();
        }

        var client = await _dbContext.Clients.FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);
        if (client == null)
        {
            return NotFound(new { error = "Client not found" });
        }

        var document = await _dbContext.ClientDocuments
            .FirstOrDefaultAsync(d => d.ClientId == clientId && d.Id == documentId, cancellationToken);

        if (document == null)
        {
            return NotFound(new { error = "Document not found" });
        }

        if (document.Status != DocumentStatus.Pending)
        {
            return Conflict(new { error = "Only pending documents can be sent for review" });
        }

        document.Status = DocumentStatus.UnderReview;
        document.RequestedAt = DateTimeOffset.UtcNow;
        document.ReviewReason = null;
        document.ReviewedAt = null;
        document.ReviewedByUserId = null;
        document.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.UPDATE);
        document.ProtheusOperation = ProtheusOperationType.UPDATE;

        if (await HasAllRequiredDocumentsReadyForReviewAsync(clientId, cancellationToken))
        {
            client.Status = ClientStatus.UnderReview;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { documentId = document.Id, status = document.Status });
    }

    [HttpPost("{documentId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid clientId, Guid documentId, [FromBody] ClientDocumentReviewRequest request, CancellationToken cancellationToken)
    {
        request ??= new ClientDocumentReviewRequest();

        var accountType = User.FindFirst("accountType")?.Value;
        if (string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var document = await _dbContext.ClientDocuments
            .FirstOrDefaultAsync(d => d.ClientId == clientId && d.Id == documentId, cancellationToken);

        if (document == null)
        {
            return NotFound(new { error = "Document not found" });
        }

        if (document.Status != DocumentStatus.UnderReview)
        {
            return Conflict(new { error = "Only documents under review can be approved" });
        }

        document.Status = DocumentStatus.Approved;
        document.ReviewReason = null;
        document.ReviewedAt = DateTimeOffset.UtcNow;
        document.ReviewedByUserId = request.ReviewedByUserId;
        document.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.UPDATE);
        document.ProtheusOperation = ProtheusOperationType.UPDATE;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { documentId = document.Id, status = document.Status });
    }

    [HttpPost("{documentId:guid}/reject")]
    public async Task<IActionResult> Reject(Guid clientId, Guid documentId, [FromBody] ClientDocumentReviewRequest request, CancellationToken cancellationToken)
    {
        request ??= new ClientDocumentReviewRequest();

        var accountType = User.FindFirst("accountType")?.Value;
        if (string.Equals(accountType, AccountTypes.Client, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "Reason is required" });
        }

        var client = await _dbContext.Clients.FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);
        if (client == null)
        {
            return NotFound(new { error = "Client not found" });
        }

        var document = await _dbContext.ClientDocuments
            .FirstOrDefaultAsync(d => d.ClientId == clientId && d.Id == documentId, cancellationToken);

        if (document == null)
        {
            return NotFound(new { error = "Document not found" });
        }

        if (document.Status != DocumentStatus.UnderReview)
        {
            return Conflict(new { error = "Only documents under review can be rejected" });
        }

        document.Status = DocumentStatus.Rejected;
        document.ReviewReason = request.Reason.Trim();
        document.ReviewedAt = DateTimeOffset.UtcNow;
        document.ReviewedByUserId = request.ReviewedByUserId;
        document.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.UPDATE);
        document.ProtheusOperation = ProtheusOperationType.UPDATE;

        client.Status = ClientStatus.PendingDocuments;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { documentId = document.Id, status = document.Status });
    }

    private bool IsClientMatch(Guid clientId)
    {
        var claim = User.FindFirst("clientId")?.Value;
        return Guid.TryParse(claim, out var parsed) && parsed == clientId;
    }

    private async Task<bool> HasAllRequiredDocumentsReadyForReviewAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var documents = await _dbContext.ClientDocuments
            .Where(d => d.ClientId == clientId && RequiredDocumentTypes.Contains(d.DocumentType))
            .ToListAsync(cancellationToken);

        foreach (var requiredType in RequiredDocumentTypes)
        {
            var document = documents.FirstOrDefault(d => d.DocumentType == requiredType);
            if (document == null)
            {
                return false;
            }

            if (document.Status != DocumentStatus.UnderReview && document.Status != DocumentStatus.Approved)
            {
                return false;
            }
        }

        return true;
    }

    private static ClientDocumentResult MapToResult(ClientDocument document)
    {
        return new ClientDocumentResult
        {
            Id = document.Id,
            DocumentType = document.DocumentType,
            Status = document.Status,
            FileName = document.FileName,
            ContentType = document.ContentType,
            SizeBytes = document.SizeBytes,
            FileUrl = document.FileUrl,
            SubmittedAt = document.SubmittedAt,
            RequestedAt = document.RequestedAt,
            ReviewedAt = document.ReviewedAt,
            ReviewedByUserId = document.ReviewedByUserId,
            ReviewReason = document.ReviewReason
        };
    }
}
