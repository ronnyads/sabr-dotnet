using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class WalletDepositService
{
    private const long MaxProofBytes = 10 * 1024 * 1024;
    private readonly IAppDbContext _db;
    private readonly WalletService _wallet;

    public WalletDepositService(IAppDbContext db, WalletService wallet)
    {
        _db = db;
        _wallet = wallet;
    }

    public async Task<ServiceResult<WalletDepositResult>> CreateAsync(
        string tenantId, Guid clientId, long amountCents, string? note,
        string fileName, string contentType, byte[] content, CancellationToken ct)
    {
        var errors = new List<ValidationError>();
        if (amountCents <= 0) errors.Add(new("amountCents", "O valor deve ser maior que zero."));
        if (amountCents > 10_000_000_000) errors.Add(new("amountCents", "O valor informado excede o limite permitido."));
        if (content.Length == 0 || content.Length > MaxProofBytes) errors.Add(new("proof", "Envie um comprovante de até 10 MB."));
        var detectedContentType = DetectContentType(content);
        if (detectedContentType == null) errors.Add(new("proof", "O comprovante deve ser um PDF, JPG ou PNG válido."));
        if (note?.Length > 500) errors.Add(new("clientNote", "A observação deve ter no máximo 500 caracteres."));
        if (errors.Count > 0) return ServiceResult<WalletDepositResult>.Failure(errors);

        var client = await _db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == clientId && x.TenantId == tenantId, ct);
        if (client == null) return ServiceResult<WalletDepositResult>.Failure(new[] { new ValidationError("client", "Cliente não encontrado.") });
        if (client.Status != ClientStatus.Approved)
            return ServiceResult<WalletDepositResult>.Failure(new[] { new ValidationError("client", "A carteira é liberada após a aprovação do cadastro.") });

        var deposit = new WalletDepositRequest
        {
            TenantId = tenantId,
            ClientId = clientId,
            AmountCents = amountCents,
            ClientNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            ProofFileName = Path.GetFileName(fileName),
            ProofContentType = detectedContentType!,
            ProofSizeBytes = content.LongLength,
            Status = WalletDepositStatus.Pending,
            Method = WalletDepositMethod.BankTransfer
        };
        deposit.Proof = new WalletDepositProof { DepositRequestId = deposit.Id, Content = content };
        _db.WalletDepositRequests.Add(deposit);
        _db.AuditEvents.Add(Audit(tenantId, clientId, "Wallet.DepositSubmitted", deposit.Id, new { amountCents, deposit.Method }));
        await _db.SaveChangesAsync(ct);
        return ServiceResult<WalletDepositResult>.Success(Map(deposit, client.AccountName));
    }

    public async Task<ClientWalletResult> GetClientWalletAsync(string tenantId, Guid clientId, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var balance = await _db.WalletAccounts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId)
            .Select(x => (long?)x.BalanceCents).FirstOrDefaultAsync(ct) ?? 0;
        var pending = await _db.WalletDepositRequests.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId && x.Status == WalletDepositStatus.Pending)
            .SumAsync(x => (long?)x.AmountCents, ct) ?? 0;
        var depositRows = await (from d in _db.WalletDepositRequests.AsNoTracking()
                                 join c in _db.Clients.AsNoTracking() on d.ClientId equals c.Id
                                 where d.TenantId == tenantId && d.ClientId == clientId
                                 orderby d.CreatedAt descending
                                 select new { Deposit = d, c.AccountName }).Take(limit).ToListAsync(ct);
        var deposits = depositRows.Select(x => Map(x.Deposit, x.AccountName)).ToList();
        var ledger = await _db.WalletLedgerEntries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ClientId == clientId)
            .OrderByDescending(x => x.CreatedAt).Take(limit)
            .Select(x => new WalletLedgerItemResult
            {
                Id = x.Id, Type = x.Type.ToString(), AmountCents = x.AmountCents,
                BalanceAfterCents = x.BalanceAfterCents, ReferenceType = x.ReferenceType,
                ReferenceId = x.ReferenceId, CreatedAt = x.CreatedAt
            }).ToListAsync(ct);
        return new ClientWalletResult { BalanceCents = balance, PendingDepositCents = pending, Deposits = deposits, Ledger = ledger };
    }

    public async Task<IReadOnlyList<WalletDepositResult>> ListAdminAsync(WalletDepositStatus? status, int skip, int limit, CancellationToken ct)
    {
        skip = Math.Max(0, skip); limit = Math.Clamp(limit, 1, 100);
        var query = from d in _db.WalletDepositRequests.AsNoTracking()
                    join c in _db.Clients.AsNoTracking() on d.ClientId equals c.Id
                    select new { d, c.AccountName };
        if (status.HasValue) query = query.Where(x => x.d.Status == status.Value);
        var rows = await query.OrderByDescending(x => x.d.CreatedAt).Skip(skip).Take(limit).ToListAsync(ct);
        return rows.Select(x => Map(x.d, x.AccountName)).ToList();
    }

    public async Task<WalletProofResult?> GetProofAsync(Guid id, string? tenantId, Guid? clientId, CancellationToken ct)
    {
        var query = _db.WalletDepositRequests.AsNoTracking().Where(x => x.Id == id);
        if (!string.IsNullOrWhiteSpace(tenantId)) query = query.Where(x => x.TenantId == tenantId);
        if (clientId.HasValue) query = query.Where(x => x.ClientId == clientId.Value);
        return await query.Join(_db.WalletDepositProofs.AsNoTracking(), d => d.Id, p => p.DepositRequestId,
            (d, p) => new WalletProofResult(d.ProofFileName, d.ProofContentType, p.Content)).FirstOrDefaultAsync(ct);
    }

    public async Task<ServiceResult<WalletDepositResult>> ApproveAsync(Guid id, Guid actorId, string? note, CancellationToken ct)
    {
        var deposit = await _db.WalletDepositRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (deposit == null) return ServiceResult<WalletDepositResult>.Failure(new[] { new ValidationError("deposit", "Depósito não encontrado.") });
        if (deposit.Status == WalletDepositStatus.Approved) return ServiceResult<WalletDepositResult>.Success(Map(deposit, await ClientName(deposit.ClientId, ct)));
        if (deposit.Status != WalletDepositStatus.Pending) return ServiceResult<WalletDepositResult>.Failure(new[] { new ValidationError("status", "Somente depósitos pendentes podem ser aprovados.") });

        var requestId = Guid.NewGuid();
        var credit = await _wallet.CreditAsync(deposit.TenantId,
            new WalletCreditRequest { ClientId = deposit.ClientId, AmountCents = deposit.AmountCents, ReferenceType = "WalletDeposit", ReferenceId = deposit.Id.ToString() },
            $"wallet-deposit:{deposit.Id:N}", requestId, actorId, "PlatformUser", ct);
        if (!credit.Succeeded || credit.Data == null) return ServiceResult<WalletDepositResult>.Failure(credit.Errors);

        deposit.Status = WalletDepositStatus.Approved;
        deposit.UpdatedAt = DateTimeOffset.UtcNow;
        deposit.ReviewedAt = DateTimeOffset.UtcNow;
        deposit.ReviewedByUserId = actorId;
        deposit.ReviewNote = CleanNote(note);
        deposit.LedgerEntryId = credit.Data.LedgerId;
        _db.AuditEvents.Add(Audit(deposit.TenantId, actorId, "Wallet.DepositApproved", deposit.Id, new { deposit.ClientId, deposit.AmountCents, deposit.LedgerEntryId }));
        await _db.SaveChangesAsync(ct);
        return ServiceResult<WalletDepositResult>.Success(Map(deposit, await ClientName(deposit.ClientId, ct)));
    }

    public async Task<ServiceResult<WalletDepositResult>> RejectAsync(Guid id, Guid actorId, string? note, CancellationToken ct)
    {
        var deposit = await _db.WalletDepositRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (deposit == null) return ServiceResult<WalletDepositResult>.Failure(new[] { new ValidationError("deposit", "Depósito não encontrado.") });
        if (deposit.Status != WalletDepositStatus.Pending) return ServiceResult<WalletDepositResult>.Failure(new[] { new ValidationError("status", "Somente depósitos pendentes podem ser rejeitados.") });
        deposit.Status = WalletDepositStatus.Rejected;
        deposit.UpdatedAt = DateTimeOffset.UtcNow;
        deposit.ReviewedAt = DateTimeOffset.UtcNow;
        deposit.ReviewedByUserId = actorId;
        deposit.ReviewNote = CleanNote(note);
        _db.AuditEvents.Add(Audit(deposit.TenantId, actorId, "Wallet.DepositRejected", deposit.Id, new { deposit.ClientId, deposit.AmountCents, deposit.ReviewNote }));
        await _db.SaveChangesAsync(ct);
        return ServiceResult<WalletDepositResult>.Success(Map(deposit, await ClientName(deposit.ClientId, ct)));
    }

    private async Task<string> ClientName(Guid clientId, CancellationToken ct) =>
        await _db.Clients.AsNoTracking().Where(x => x.Id == clientId).Select(x => x.AccountName).FirstOrDefaultAsync(ct) ?? "Cliente";
    private static string? DetectContentType(byte[] content)
    {
        if (content.Length >= 4 && content[0] == 0x25 && content[1] == 0x50 && content[2] == 0x44 && content[3] == 0x46) return "application/pdf";
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF) return "image/jpeg";
        if (content.Length >= 8 && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47 && content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A) return "image/png";
        return null;
    }
    private static string? CleanNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)];
    private static WalletDepositResult Map(WalletDepositRequest d, string? name) => new() { Id=d.Id,TenantId=d.TenantId,ClientId=d.ClientId,ClientName=name??"Cliente",AmountCents=d.AmountCents,Method=d.Method.ToString(),Status=d.Status.ToString(),ProofFileName=d.ProofFileName,ClientNote=d.ClientNote,ReviewNote=d.ReviewNote,CreatedAt=d.CreatedAt,ReviewedAt=d.ReviewedAt,LedgerEntryId=d.LedgerEntryId };
    private static AuditEvent Audit(string tenantId, Guid? actorId, string action, Guid entityId, object metadata) => new() { TenantId=tenantId,ActorType=action.Contains("Submitted")?"TenantUser":"PlatformUser",ActorId=actorId,Action=action,Entity=nameof(WalletDepositRequest),EntityId=entityId,RequestId=Guid.NewGuid(),MetadataJson=JsonSerializer.Serialize(metadata) };
}
