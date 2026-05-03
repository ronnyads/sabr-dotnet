using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
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
