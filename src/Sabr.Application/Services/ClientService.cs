using Microsoft.EntityFrameworkCore;
using Sabr.Application.Abstractions;
using Sabr.Application.Models;
using Sabr.Application.Security;
using Sabr.Application.Validation;
using Sabr.Domain.Entities;
using Sabr.Domain.Enums;
using Sabr.Domain.Protheus;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sabr.Application.Services;

public sealed class ClientService
{
    private const string DefaultStoreCode = "01";
    private const int ProtheusCodeLength = 6;
    private const int MinPasswordLength = 8;

    private readonly IAppDbContext _dbContext;
    private readonly ICepLookup _cepLookup;

    public ClientService(IAppDbContext dbContext, ICepLookup cepLookup)
    {
        _dbContext = dbContext;
        _cepLookup = cepLookup;
    }

    public async Task<ServiceResult<ClientRegistrationResult>> RegisterPublicAsync(
        ClientPublicRegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateClientRequest(request);
        var normalizedDocument = BrazilValidators.OnlyDigits(request.Document);
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        if (await _dbContext.Clients.AnyAsync(c => c.Document == normalizedDocument, cancellationToken))
        {
            errors.Add(new ValidationError("document", "Document already registered"));
        }

        if (await _dbContext.Clients.AnyAsync(c => c.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (await _dbContext.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        await ValidateCepAsync(request.ZipCode, errors, cancellationToken);

        if (errors.Count > 0)
        {
            return ServiceResult<ClientRegistrationResult>.Failure(errors);
        }

        var protheusCode = await GenerateProtheusCodeAsync(cancellationToken);
        if (await _dbContext.Tenants.AnyAsync(t => t.Slug == protheusCode, cancellationToken))
        {
            return ServiceResult<ClientRegistrationResult>.Failure(new[]
            {
                new ValidationError("protheusCode", "Generated code already exists")
            });
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.LegalName.Trim(),
            Slug = protheusCode,
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var client = new Client
        {
            PersonType = request.PersonType,
            ProtheusCode = protheusCode,
            AccountName = request.ResponsibleName.Trim(),
            PasswordHash = PasswordHasher.HashPassword(Guid.NewGuid().ToString("N")),
            MustChangePassword = true,
            LegalName = request.LegalName.Trim(),
            TradeName = request.TradeName?.Trim(),
            Document = normalizedDocument,
            StateRegistration = NormalizeStateRegistration(request.StateRegistration, request.IsStateRegistrationExempt || IsIsento(request.StateRegistration)),
            IsStateRegistrationExempt = request.IsStateRegistrationExempt || IsIsento(request.StateRegistration),
            Email = normalizedEmail,
            Whatsapp = BrazilValidators.OnlyDigits(request.Whatsapp),
            Phone = request.Phone != null ? BrazilValidators.OnlyDigits(request.Phone) : null,
            BirthDate = request.BirthDate,
            ZipCode = BrazilValidators.OnlyDigits(request.ZipCode),
            Street = request.Street.Trim(),
            Number = request.Number.Trim(),
            District = request.District.Trim(),
            City = request.City.Trim(),
            State = request.State.Trim().ToUpperInvariant(),
            Complement = request.Complement?.Trim(),
            ResponsibleName = request.ResponsibleName.Trim(),
            ResponsibleDocument = BrazilValidators.OnlyDigits(request.ResponsibleDocument),
            Status = ClientStatus.PendingAdminApproval,
            TenantId = tenant.Id,
            ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE),
            ProtheusOperation = ProtheusOperationType.CREATE
        };

        var store = new ClientStore
        {
            ClientId = client.Id,
            TenantId = tenant.Id,
            StoreCode = DefaultStoreCode,
            IsActive = true,
            ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE),
            ProtheusOperation = ProtheusOperationType.CREATE
        };

        _dbContext.Tenants.Add(tenant);
        _dbContext.Clients.Add(client);
        _dbContext.ClientStores.Add(store);
        QueueOutboxEvent(client.TenantId, nameof(Client), client.Id, "ClientCreated", new
        {
            client.Id,
            client.TenantId,
            client.Email,
            client.Status
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<ClientRegistrationResult>.Success(new ClientRegistrationResult
        {
            ClientId = client.Id,
            TenantId = client.TenantId,
            Status = client.Status
        });
    }

    public async Task<ServiceResult<ClientSeedResult>> CreateByAdminAsync(
        ClientSeedRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateSeedRequest(request);
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        if (await _dbContext.Clients.AnyAsync(c => c.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (await _dbContext.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (errors.Count > 0)
        {
            return ServiceResult<ClientSeedResult>.Failure(errors);
        }

        var protheusCode = await GenerateProtheusCodeAsync(cancellationToken);
        if (await _dbContext.Tenants.AnyAsync(t => t.Slug == protheusCode, cancellationToken))
        {
            return ServiceResult<ClientSeedResult>.Failure(new[]
            {
                new ValidationError("protheusCode", "Generated code already exists")
            });
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.AccountName.Trim(),
            Slug = protheusCode,
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var client = new Client
        {
            PersonType = PersonType.CNPJ,
            ProtheusCode = protheusCode,
            AccountName = request.AccountName.Trim(),
            Email = normalizedEmail,
            PasswordHash = PasswordHasher.HashPassword(request.TemporaryPassword),
            MustChangePassword = true,
            Status = ClientStatus.PendingProfile,
            TenantId = tenant.Id,
            ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE),
            ProtheusOperation = ProtheusOperationType.CREATE
        };

        var store = new ClientStore
        {
            ClientId = client.Id,
            TenantId = tenant.Id,
            StoreCode = DefaultStoreCode,
            IsActive = true,
            ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE),
            ProtheusOperation = ProtheusOperationType.CREATE
        };

        _dbContext.Tenants.Add(tenant);
        _dbContext.Clients.Add(client);
        _dbContext.ClientStores.Add(store);
        QueueOutboxEvent(client.TenantId, nameof(Client), client.Id, "ClientCreated", new
        {
            client.Id,
            client.TenantId,
            client.Email,
            client.Status
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<ClientSeedResult>.Success(new ClientSeedResult
        {
            ClientId = client.Id,
            TenantId = client.TenantId,
            ProtheusCode = client.ProtheusCode,
            AccountName = client.AccountName,
            Email = client.Email,
            Status = client.Status
        });
    }

    public async Task<ServiceResult<ClientApprovalResult>> ApproveAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        var client = await _dbContext.Clients.FirstOrDefaultAsync(s => s.Id == clientId, cancellationToken);
        if (client == null)
        {
            return ServiceResult<ClientApprovalResult>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        if (client.Status == ClientStatus.Inactive)
        {
            return ServiceResult<ClientApprovalResult>.Failure(new[]
            {
                new ValidationError("status", "Inactive client cannot be approved")
            });
        }

        var now = DateTimeOffset.UtcNow;
        switch (client.Status)
        {
            case ClientStatus.PendingProfile:
            case ClientStatus.PendingAdminApproval:
                // Initial platform approval to allow onboarding/docs flow.
                client.Status = ClientStatus.PendingDocuments;
                break;
            case ClientStatus.PendingDocuments:
            case ClientStatus.UnderReview:
            case ClientStatus.Rejected:
            case ClientStatus.Approved:
                // Final approval after docs review (idempotent when already approved).
                client.Status = ClientStatus.Approved;
                break;
        }

        client.ApprovedAt = now;
        client.RejectedAt = null;
        client.RejectionReason = null;
        client.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.UPDATE);
        client.ProtheusOperation = ProtheusOperationType.UPDATE;
        QueueOutboxEvent(client.TenantId, nameof(Client), client.Id, "ClientUpdated", new
        {
            client.Id,
            client.TenantId,
            client.Status,
            client.ApprovedAt
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<ClientApprovalResult>.Success(new ClientApprovalResult
        {
            ClientId = client.Id,
            Status = client.Status,
            ApprovedAt = client.ApprovedAt
        });
    }

    public async Task<ServiceResult<ClientApprovalResult>> RejectAsync(Guid clientId, string reason, CancellationToken cancellationToken = default)
    {
        var client = await _dbContext.Clients.FirstOrDefaultAsync(s => s.Id == clientId, cancellationToken);
        if (client == null)
        {
            return ServiceResult<ClientApprovalResult>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ServiceResult<ClientApprovalResult>.Failure(new[]
            {
                new ValidationError("reason", "Rejection reason is required")
            });
        }

        client.Status = ClientStatus.Rejected;
        client.RejectedAt = DateTimeOffset.UtcNow;
        client.RejectionReason = reason.Trim();
        client.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CANCEL);
        client.ProtheusOperation = ProtheusOperationType.CANCEL;
        QueueOutboxEvent(client.TenantId, nameof(Client), client.Id, "ClientUpdated", new
        {
            client.Id,
            client.TenantId,
            client.Status,
            client.RejectedAt,
            client.RejectionReason
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<ClientApprovalResult>.Success(new ClientApprovalResult
        {
            ClientId = client.Id,
            Status = client.Status,
            RejectedAt = client.RejectedAt,
            RejectionReason = client.RejectionReason
        });
    }

    public async Task<ServiceResult<ClientResult>> GetAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        var client = await _dbContext.Clients.FirstOrDefaultAsync(s => s.Id == clientId, cancellationToken);
        if (client == null)
        {
            return ServiceResult<ClientResult>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        var tenantSlug = await _dbContext.Tenants
            .Where(t => t.Id == client.TenantId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync(cancellationToken);

        return ServiceResult<ClientResult>.Success(MapToResult(client, tenantSlug));
    }

    public async Task<ServiceResult<ClientResetPasswordResult>> ResetPasswordAsync(
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        var client = await _dbContext.Clients.FirstOrDefaultAsync(s => s.Id == clientId, cancellationToken);
        if (client == null)
        {
            return ServiceResult<ClientResetPasswordResult>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        var temporaryPassword = GenerateTemporaryPassword();
        client.PasswordHash = PasswordHasher.HashPassword(temporaryPassword);
        client.MustChangePassword = true;
        QueueOutboxEvent(client.TenantId, nameof(Client), client.Id, "ClientUpdated", new
        {
            client.Id,
            client.TenantId,
            client.Status,
            client.MustChangePassword
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ServiceResult<ClientResetPasswordResult>.Success(new ClientResetPasswordResult
        {
            ClientId = client.Id,
            TemporaryPassword = temporaryPassword
        });
    }

    public async Task<ServiceResult<ClientListResponse>> ListAsync(
        int skip,
        int limit,
        ClientStatus? status,
        string? search,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Clients.AsQueryable();

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            query = query.Where(s => s.TenantId == tenantId);
        }

        if (status.HasValue)
        {
            query = query.Where(s => s.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(s =>
                (s.LegalName != null && s.LegalName.ToLower().Contains(term)) ||
                (s.TradeName != null && s.TradeName.ToLower().Contains(term)) ||
                (s.Document != null && s.Document.ToLower().Contains(term)) ||
                s.Email.ToLower().Contains(term) ||
                s.ProtheusCode.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip(skip)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var tenantIds = items.Select(item => item.TenantId).Distinct().ToList();
        var tenantSlugs = await _dbContext.Tenants
            .Where(tenant => tenantIds.Contains(tenant.Id))
            .ToDictionaryAsync(tenant => tenant.Id, tenant => tenant.Slug, cancellationToken);

        return ServiceResult<ClientListResponse>.Success(new ClientListResponse
        {
            Items = items.Select(item =>
            {
                tenantSlugs.TryGetValue(item.TenantId, out var slug);
                return MapToResult(item, slug);
            }).ToList(),
            Total = total,
            Skip = skip,
            Limit = limit
        });
    }

    public async Task<ServiceResult<ClientResult>> UpdateAsync(
        Guid clientId,
        ClientUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateClientRequest(request);
        var normalizedDocument = BrazilValidators.OnlyDigits(request.Document);
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var client = await _dbContext.Clients.FirstOrDefaultAsync(s => s.Id == clientId, cancellationToken);
        if (client == null)
        {
            return ServiceResult<ClientResult>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        if (await _dbContext.Clients.AnyAsync(s => s.Document == normalizedDocument && s.Id != clientId, cancellationToken))
        {
            errors.Add(new ValidationError("document", "Document already registered"));
        }

        if (await _dbContext.Clients.AnyAsync(s => s.Email == normalizedEmail && s.Id != clientId, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (await _dbContext.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        await ValidateCepAsync(request.ZipCode, errors, cancellationToken);

        if (errors.Count > 0)
        {
            return ServiceResult<ClientResult>.Failure(errors);
        }

        client.PersonType = request.PersonType;
        client.LegalName = request.LegalName.Trim();
        client.TradeName = request.TradeName?.Trim();
        client.Document = normalizedDocument;
        client.StateRegistration = NormalizeStateRegistration(request.StateRegistration, request.IsStateRegistrationExempt || IsIsento(request.StateRegistration));
        client.IsStateRegistrationExempt = request.IsStateRegistrationExempt || IsIsento(request.StateRegistration);
        client.Email = normalizedEmail;
        client.Whatsapp = BrazilValidators.OnlyDigits(request.Whatsapp);
        client.Phone = request.Phone != null ? BrazilValidators.OnlyDigits(request.Phone) : null;
        client.BirthDate = request.BirthDate;
        client.ZipCode = BrazilValidators.OnlyDigits(request.ZipCode);
        client.Street = request.Street.Trim();
        client.Number = request.Number.Trim();
        client.District = request.District.Trim();
        client.City = request.City.Trim();
        client.State = request.State.Trim().ToUpperInvariant();
        client.Complement = request.Complement?.Trim();
        client.ResponsibleName = request.ResponsibleName.Trim();
        client.ResponsibleDocument = BrazilValidators.OnlyDigits(request.ResponsibleDocument);

        client.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.UPDATE);
        client.ProtheusOperation = ProtheusOperationType.UPDATE;
        QueueOutboxEvent(client.TenantId, nameof(Client), client.Id, "ClientUpdated", new
        {
            client.Id,
            client.TenantId,
            client.Email,
            client.Status
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        var tenantSlug = await _dbContext.Tenants
            .Where(t => t.Id == client.TenantId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync(cancellationToken);

        return ServiceResult<ClientResult>.Success(MapToResult(client, tenantSlug));
    }

    public async Task<ServiceResult<ClientResult>> CompleteProfileAsync(
        Guid clientId,
        ClientUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateClientRequest(request);
        var normalizedDocument = BrazilValidators.OnlyDigits(request.Document);
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var client = await _dbContext.Clients.FirstOrDefaultAsync(s => s.Id == clientId, cancellationToken);
        if (client == null || client.Status == ClientStatus.Inactive)
        {
            return ServiceResult<ClientResult>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        if (await _dbContext.Clients.AnyAsync(s => s.Document == normalizedDocument && s.Id != clientId, cancellationToken))
        {
            errors.Add(new ValidationError("document", "Document already registered"));
        }

        if (await _dbContext.Clients.AnyAsync(s => s.Email == normalizedEmail && s.Id != clientId, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        if (await _dbContext.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken))
        {
            errors.Add(new ValidationError("email", "Email already registered"));
        }

        await ValidateCepAsync(request.ZipCode, errors, cancellationToken);

        if (errors.Count > 0)
        {
            return ServiceResult<ClientResult>.Failure(errors);
        }

        client.PersonType = request.PersonType;
        client.LegalName = request.LegalName.Trim();
        client.TradeName = request.TradeName?.Trim();
        client.Document = normalizedDocument;
        client.StateRegistration = NormalizeStateRegistration(request.StateRegistration, request.IsStateRegistrationExempt || IsIsento(request.StateRegistration));
        client.IsStateRegistrationExempt = request.IsStateRegistrationExempt || IsIsento(request.StateRegistration);
        client.Email = normalizedEmail;
        client.Whatsapp = BrazilValidators.OnlyDigits(request.Whatsapp);
        client.Phone = request.Phone != null ? BrazilValidators.OnlyDigits(request.Phone) : null;
        client.BirthDate = request.BirthDate;
        client.ZipCode = BrazilValidators.OnlyDigits(request.ZipCode);
        client.Street = request.Street.Trim();
        client.Number = request.Number.Trim();
        client.District = request.District.Trim();
        client.City = request.City.Trim();
        client.State = request.State.Trim().ToUpperInvariant();
        client.Complement = request.Complement?.Trim();
        client.ResponsibleName = request.ResponsibleName.Trim();
        client.ResponsibleDocument = BrazilValidators.OnlyDigits(request.ResponsibleDocument);
        client.ProfileCompletedAt = DateTimeOffset.UtcNow;
        client.Status = client.Status switch
        {
            ClientStatus.UnderReview => ClientStatus.UnderReview,
            ClientStatus.Approved => ClientStatus.Approved,
            ClientStatus.Rejected => ClientStatus.Rejected,
            ClientStatus.PendingProfile => ClientStatus.PendingDocuments,
            ClientStatus.PendingAdminApproval => ClientStatus.PendingDocuments,
            ClientStatus.PendingDocuments => ClientStatus.PendingDocuments,
            _ => client.Status
        };
        client.OnboardingStep = 3;
        client.RejectedAt = null;
        client.RejectionReason = null;
        client.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.UPDATE);
        client.ProtheusOperation = ProtheusOperationType.UPDATE;

        var tenant = await _dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == client.TenantId, cancellationToken);
        if (tenant != null)
        {
            tenant.Name = client.LegalName ?? tenant.Name;
        }

        QueueOutboxEvent(client.TenantId, nameof(Client), client.Id, "ClientUpdated", new
        {
            client.Id,
            client.TenantId,
            client.Email,
            client.Status,
            client.OnboardingStep
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        var tenantSlug = await _dbContext.Tenants
            .Where(t => t.Id == client.TenantId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync(cancellationToken);

        return ServiceResult<ClientResult>.Success(MapToResult(client, tenantSlug));
    }

    public async Task<ServiceResult<bool>> SetOnboardingStepAsync(
        Guid clientId,
        int step,
        CancellationToken cancellationToken = default)
    {
        if (step < 0 || step > 3)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("step", "Step must be between 0 and 3")
            });
        }

        var client = await _dbContext.Clients.FirstOrDefaultAsync(s => s.Id == clientId, cancellationToken);
        if (client == null || client.Status == ClientStatus.Inactive)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        client.OnboardingStep = step;
        client.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.UPDATE);
        client.ProtheusOperation = ProtheusOperationType.UPDATE;
        QueueOutboxEvent(client.TenantId, nameof(Client), client.Id, "ClientUpdated", new
        {
            client.Id,
            client.TenantId,
            client.Status,
            client.OnboardingStep
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<bool>> DeactivateAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        var client = await _dbContext.Clients.FirstOrDefaultAsync(s => s.Id == clientId, cancellationToken);
        if (client == null)
        {
            return ServiceResult<bool>.Failure(new[]
            {
                new ValidationError("clientId", "Client not found")
            });
        }

        client.Status = ClientStatus.Inactive;
        client.ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CANCEL);
        client.ProtheusOperation = ProtheusOperationType.CANCEL;
        QueueOutboxEvent(client.TenantId, nameof(Client), client.Id, "ClientUpdated", new
        {
            client.Id,
            client.TenantId,
            client.Status
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    private void QueueOutboxEvent(string tenantId, string aggregateType, Guid aggregateId, string eventType, object payload)
    {
        _dbContext.ProtheusOutboxEvents.Add(new ProtheusOutboxEvent
        {
            TenantId = tenantId,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload),
            Status = OutboxStatus.Pending,
            Attempts = 0,
            CorrelationId = Guid.NewGuid()
        });
    }

    private async Task<string> GenerateProtheusCodeAsync(CancellationToken cancellationToken)
    {
        var nextValue = await _dbContext.NextClientProtheusCodeAsync(cancellationToken);
        return nextValue.ToString($"D{ProtheusCodeLength}");
    }

    private static List<ValidationError> ValidateSeedRequest(ClientSeedRequest request)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(request.AccountName))
            errors.Add(new ValidationError("accountName", "Account name is required"));

        if (!BrazilValidators.IsValidEmail(request.Email))
            errors.Add(new ValidationError("email", "Invalid email"));

        if (string.IsNullOrWhiteSpace(request.TemporaryPassword) || request.TemporaryPassword.Length < MinPasswordLength)
            errors.Add(new ValidationError("temporaryPassword", $"Password must be at least {MinPasswordLength} characters"));

        return errors;
    }

    private static List<ValidationError> ValidateClientRequest(ClientPublicRegisterRequest request)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(request.LegalName))
            errors.Add(new ValidationError("legalName", "Legal name is required"));

        if (string.IsNullOrWhiteSpace(request.Document))
            errors.Add(new ValidationError("document", "Document is required"));

        if (!BrazilValidators.IsValidEmail(request.Email))
            errors.Add(new ValidationError("email", "Invalid email"));

        if (!BrazilValidators.IsValidWhatsapp(request.Whatsapp))
            errors.Add(new ValidationError("whatsapp", "Invalid WhatsApp"));

        if (!BrazilValidators.IsValidCep(request.ZipCode))
            errors.Add(new ValidationError("zipCode", "Invalid CEP"));

        if (string.IsNullOrWhiteSpace(request.Street))
            errors.Add(new ValidationError("street", "Street is required"));

        if (string.IsNullOrWhiteSpace(request.Number))
            errors.Add(new ValidationError("number", "Number is required"));

        if (string.IsNullOrWhiteSpace(request.District))
            errors.Add(new ValidationError("district", "District is required"));

        if (string.IsNullOrWhiteSpace(request.City))
            errors.Add(new ValidationError("city", "City is required"));

        if (!BrazilValidators.IsValidUF(request.State))
            errors.Add(new ValidationError("state", "Invalid UF"));

        if (string.IsNullOrWhiteSpace(request.ResponsibleName))
            errors.Add(new ValidationError("responsibleName", "Responsible name is required"));

        if (string.IsNullOrWhiteSpace(request.ResponsibleDocument))
            errors.Add(new ValidationError("responsibleDocument", "Responsible document is required"));
        else if (!BrazilValidators.IsValidCpf(request.ResponsibleDocument))
            errors.Add(new ValidationError("responsibleDocument", "Responsible CPF is invalid"));

        if (request.PersonType == PersonType.CPF)
        {
            if (!BrazilValidators.IsValidCpf(request.Document))
                errors.Add(new ValidationError("document", "Invalid CPF"));
        }
        else
        {
            if (!BrazilValidators.IsValidCnpj(request.Document))
                errors.Add(new ValidationError("document", "Invalid CNPJ"));
            var isExempt = request.IsStateRegistrationExempt || IsIsento(request.StateRegistration);
            if (string.IsNullOrWhiteSpace(request.StateRegistration))
                errors.Add(new ValidationError("stateRegistration", "State registration is required"));
            else if (!isExempt && !BrazilValidators.IsValidInscricaoEstadual(request.StateRegistration, request.State, false))
                errors.Add(new ValidationError("stateRegistration", $"Invalid IE for UF {request.State}"));
        }

        return errors;
    }

    private static List<ValidationError> ValidateClientRequest(ClientUpdateRequest request)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(request.LegalName))
            errors.Add(new ValidationError("legalName", "Legal name is required"));

        if (string.IsNullOrWhiteSpace(request.Document))
            errors.Add(new ValidationError("document", "Document is required"));

        if (!BrazilValidators.IsValidEmail(request.Email))
            errors.Add(new ValidationError("email", "Invalid email"));

        if (!BrazilValidators.IsValidWhatsapp(request.Whatsapp))
            errors.Add(new ValidationError("whatsapp", "Invalid WhatsApp"));

        if (!BrazilValidators.IsValidCep(request.ZipCode))
            errors.Add(new ValidationError("zipCode", "Invalid CEP"));

        if (string.IsNullOrWhiteSpace(request.Street))
            errors.Add(new ValidationError("street", "Street is required"));

        if (string.IsNullOrWhiteSpace(request.Number))
            errors.Add(new ValidationError("number", "Number is required"));

        if (string.IsNullOrWhiteSpace(request.District))
            errors.Add(new ValidationError("district", "District is required"));

        if (string.IsNullOrWhiteSpace(request.City))
            errors.Add(new ValidationError("city", "City is required"));

        if (!BrazilValidators.IsValidUF(request.State))
            errors.Add(new ValidationError("state", "Invalid UF"));

        if (string.IsNullOrWhiteSpace(request.ResponsibleName))
            errors.Add(new ValidationError("responsibleName", "Responsible name is required"));

        if (string.IsNullOrWhiteSpace(request.ResponsibleDocument))
            errors.Add(new ValidationError("responsibleDocument", "Responsible document is required"));
        else if (!BrazilValidators.IsValidCpf(request.ResponsibleDocument))
            errors.Add(new ValidationError("responsibleDocument", "Responsible CPF is invalid"));

        if (request.PersonType == PersonType.CPF)
        {
            if (!BrazilValidators.IsValidCpf(request.Document))
                errors.Add(new ValidationError("document", "Invalid CPF"));
        }
        else
        {
            if (!BrazilValidators.IsValidCnpj(request.Document))
                errors.Add(new ValidationError("document", "Invalid CNPJ"));
            var isExempt = request.IsStateRegistrationExempt || IsIsento(request.StateRegistration);
            if (string.IsNullOrWhiteSpace(request.StateRegistration))
                errors.Add(new ValidationError("stateRegistration", "State registration is required"));
            else if (!isExempt && !BrazilValidators.IsValidInscricaoEstadual(request.StateRegistration, request.State, false))
                errors.Add(new ValidationError("stateRegistration", $"Invalid IE for UF {request.State}"));
        }

        return errors;
    }

    private async Task ValidateCepAsync(string cep, List<ValidationError> errors, CancellationToken cancellationToken)
    {
        if (errors.Any(e => string.Equals(e.Field, "zipCode", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var lookup = await _cepLookup.LookupAsync(cep, cancellationToken);
        if (lookup.Status == CepLookupStatus.NotFound)
        {
            errors.Add(new ValidationError("zipCode", "CEP inexistente"));
        }
        else if (lookup.Status == CepLookupStatus.Unavailable)
        {
            errors.Add(new ValidationError("zipCode", "Servico de CEP indisponivel"));
        }
    }

    private static string? NormalizeStateRegistration(string? value, bool isExempt)
    {
        if (isExempt)
        {
            return "ISENTO";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (IsIsento(value))
        {
            return "ISENTO";
        }

        return BrazilValidators.OnlyDigits(value);
    }

    private static bool IsIsento(string? value)
    {
        return string.Equals(value?.Trim(), "ISENTO", StringComparison.OrdinalIgnoreCase);
    }

    private static ClientResult MapToResult(Client client, string? tenantSlug = null)
    {
        return new ClientResult
        {
            Id = client.Id,
            TenantId = client.TenantId,
            TenantSlug = tenantSlug,
            ProtheusCode = client.ProtheusCode,
            AccountName = client.AccountName,
            PersonType = client.PersonType,
            LegalName = client.LegalName,
            TradeName = client.TradeName,
            Document = client.Document,
            StateRegistration = client.StateRegistration,
            IsStateRegistrationExempt = client.IsStateRegistrationExempt,
            Email = client.Email,
            Whatsapp = client.Whatsapp,
            Phone = client.Phone,
            BirthDate = client.BirthDate,
            ZipCode = client.ZipCode,
            Street = client.Street,
            Number = client.Number,
            District = client.District,
            City = client.City,
            State = client.State,
            Complement = client.Complement,
            ResponsibleName = client.ResponsibleName,
            ResponsibleDocument = client.ResponsibleDocument,
            Status = client.Status,
            ApprovedAt = client.ApprovedAt,
            RejectedAt = client.RejectedAt,
            RejectionReason = client.RejectionReason,
            MustChangePassword = client.MustChangePassword,
            ProfileCompletedAt = client.ProfileCompletedAt,
            LastLoginAt = client.LastLoginAt,
            IsActive = client.Status != ClientStatus.Inactive
        };
    }

    private static string GenerateTemporaryPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#$";
        var bytes = RandomNumberGenerator.GetBytes(12);
        var builder = new StringBuilder(12);

        foreach (var value in bytes)
        {
            builder.Append(chars[value % chars.Length]);
        }

        return builder.ToString();
    }
}
