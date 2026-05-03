using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Security;
using Phub.Application.Validation;
using Phub.Domain.Entities;
using Phub.Domain.Enums;

namespace Phub.Application.Services;

public sealed class AdminSupplierService
{
    private readonly IAppDbContext _dbContext;

    public AdminSupplierService(IAppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ServiceResult<List<SupplierResult>>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var suppliers = await _dbContext.Suppliers
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        return ServiceResult<List<SupplierResult>>.Success(suppliers.Select(MapResult).ToList());
    }

    public async Task<ServiceResult<SupplierResult>> GetAsync(
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var supplier = await _dbContext.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);

        if (supplier == null)
            return ServiceResult<SupplierResult>.NotFound("id", "Supplier not found");

        return ServiceResult<SupplierResult>.Success(MapResult(supplier));
    }

    public async Task<ServiceResult<SupplierResult>> ActivateAsync(
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var supplier = await _dbContext.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);

        if (supplier == null)
            return ServiceResult<SupplierResult>.NotFound("id", "Supplier not found");

        supplier.Status = SupplierStatus.Active;
        supplier.IsActive = true;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierResult>.Success(MapResult(supplier));
    }

    public async Task<ServiceResult<SupplierResult>> SuspendAsync(
        Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        var supplier = await _dbContext.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);

        if (supplier == null)
            return ServiceResult<SupplierResult>.NotFound("id", "Supplier not found");

        supplier.Status = SupplierStatus.Suspended;
        supplier.IsActive = false;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierResult>.Success(MapResult(supplier));
    }

    public async Task<ServiceResult<SupplierResult>> CreateAsync(
        AdminCreateSupplierRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add(new ValidationError("name", "Name is required"));
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            errors.Add(new ValidationError("email", "Invalid email"));
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            errors.Add(new ValidationError("password", "Password must be at least 8 characters"));
        if (errors.Count > 0)
            return ServiceResult<SupplierResult>.Failure(errors);

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var exists = await _dbContext.Suppliers
            .AnyAsync(s => s.EmailNormalized == normalizedEmail, cancellationToken);
        if (exists)
            return ServiceResult<SupplierResult>.Failure(new[] { new ValidationError("email", "Email already registered") });

        var supplier = new Supplier
        {
            Name = request.Name.Trim(),
            Email = request.Email.Trim(),
            EmailNormalized = normalizedEmail,
            PasswordHash = PasswordHasher.HashPassword(request.Password),
            Status = SupplierStatus.Active,
            IsActive = true,
            CompanyName = request.CompanyName?.Trim(),
            Document = request.Document?.Trim(),
            Phone = request.Phone?.Trim()
        };

        _dbContext.Suppliers.Add(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return ServiceResult<SupplierResult>.Success(MapResult(supplier));
    }

    private static SupplierResult MapResult(Supplier s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Email = s.Email,
        CompanyName = s.CompanyName,
        Phone = s.Phone,
        Document = s.Document,
        Status = s.Status.ToString(),
        IsActive = s.IsActive,
        CreatedAt = s.CreatedAt,
        LastLoginAt = s.LastLoginAt
    };
}
