using Microsoft.EntityFrameworkCore;
using Phub.Application.Abstractions;
using Phub.Application.Models;
using Phub.Application.Security;
using Phub.Application.Services;
using Phub.Domain.Entities;
using Phub.Domain.Enums;
using Phub.Domain.Protheus;
using Phub.Infrastructure.Persistence;
using Phub.Infrastructure.Services;

namespace Phub.Api.Tests;

public sealed class ClientServiceCnpjWarningTests
{
    [Fact]
    public async Task CompleteProfile_WithCpf_ClearsOutsideSpWarningFields()
    {
        await using var db = CreateDb();
        var client = await SeedClientAsync(db, configure: seeded =>
        {
            seeded.PersonType = PersonType.CNPJ;
            seeded.Document = "11222333000181";
            seeded.CnpjUf = "RJ";
            seeded.IsCnpjOutsideSp = true;
            seeded.OutOfSpCnpjWarningAccepted = true;
            seeded.OutOfSpCnpjWarningAcceptedAt = DateTimeOffset.UtcNow.AddDays(-1);
        });

        var service = CreateService(db);
        var result = await service.CompleteProfileAsync(client.Id, BuildCpfRequest(client.Email));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Null(result.Data!.CnpjUf);
        Assert.False(result.Data.IsCnpjOutsideSp);
        Assert.False(result.Data.OutOfSpCnpjWarningAccepted);
        Assert.Null(result.Data.OutOfSpCnpjWarningAcceptedAt);
    }

    [Fact]
    public async Task CompleteProfile_WithSpCnpj_DoesNotRequireWarningAcceptance()
    {
        await using var db = CreateDb();
        var client = await SeedClientAsync(db);

        var service = CreateService(db);
        var result = await service.CompleteProfileAsync(client.Id, BuildCnpjRequest(client.Email, "60355549000120", "SP"));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("SP", result.Data!.CnpjUf);
        Assert.False(result.Data.IsCnpjOutsideSp);
        Assert.False(result.Data.OutOfSpCnpjWarningAccepted);
        Assert.Null(result.Data.OutOfSpCnpjWarningAcceptedAt);
    }

    [Fact]
    public async Task CompleteProfile_WithOutsideSpCnpj_WithoutAcceptance_ReturnsValidationError()
    {
        await using var db = CreateDb();
        var client = await SeedClientAsync(db);

        var service = CreateService(db);
        var result = await service.CompleteProfileAsync(client.Id, BuildCnpjRequest(client.Email, "11222333000181", "RJ"));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            error => string.Equals(error.Field, "outOfSpCnpjWarningAccepted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompleteProfile_WithOutsideSpCnpj_WithAcceptance_PersistsWarningFields()
    {
        await using var db = CreateDb();
        var client = await SeedClientAsync(db);

        var service = CreateService(db);
        var result = await service.CompleteProfileAsync(
            client.Id,
            BuildCnpjRequest(client.Email, "11222333000181", "RJ", outOfSpCnpjWarningAccepted: true));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("RJ", result.Data!.CnpjUf);
        Assert.True(result.Data.IsCnpjOutsideSp);
        Assert.True(result.Data.OutOfSpCnpjWarningAccepted);
        Assert.NotNull(result.Data.OutOfSpCnpjWarningAcceptedAt);
    }

    [Fact]
    public async Task CompleteProfile_WithSameOutsideSpScenario_KeepsExistingAcceptance()
    {
        await using var db = CreateDb();
        var acceptedAt = DateTimeOffset.UtcNow.AddDays(-3);
        var client = await SeedClientAsync(db, configure: seeded =>
        {
            seeded.PersonType = PersonType.CNPJ;
            seeded.Document = "11222333000181";
            seeded.CnpjUf = "RJ";
            seeded.IsCnpjOutsideSp = true;
            seeded.OutOfSpCnpjWarningAccepted = true;
            seeded.OutOfSpCnpjWarningAcceptedAt = acceptedAt;
        });

        var service = CreateService(db);
        var result = await service.CompleteProfileAsync(client.Id, BuildCnpjRequest(client.Email, "11222333000181", "RJ"));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.OutOfSpCnpjWarningAccepted);
        Assert.Equal(acceptedAt.ToUnixTimeSeconds(), result.Data.OutOfSpCnpjWarningAcceptedAt?.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task CompleteProfile_WhenLookupIsUnavailable_FallsBackToRequestState()
    {
        await using var db = CreateDb();
        var client = await SeedClientAsync(db);

        var service = CreateService(db);
        var result = await service.CompleteProfileAsync(
            client.Id,
            BuildCnpjRequest(client.Email, "33444555000181", "MG", outOfSpCnpjWarningAccepted: true));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("MG", result.Data!.CnpjUf);
        Assert.True(result.Data.IsCnpjOutsideSp);
        Assert.True(result.Data.OutOfSpCnpjWarningAccepted);
    }

    [Fact]
    public async Task CompleteProfile_WithLookupUfDifferentFromAddressState_KeepsAddressStateAndUsesFiscalUf()
    {
        await using var db = CreateDb();
        var client = await SeedClientAsync(db);

        var service = CreateService(db);
        var result = await service.CompleteProfileAsync(
            client.Id,
            BuildCnpjRequest(
                client.Email,
                "11222333000181",
                "SP",
                outOfSpCnpjWarningAccepted: true));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("SP", result.Data!.State);
        Assert.Equal("RJ", result.Data.CnpjUf);
        Assert.True(result.Data.IsCnpjOutsideSp);
    }

    [Fact]
    public async Task CompleteProfile_WithLookupUfDifferentFromAddressState_UsesFiscalUfForIeValidation()
    {
        await using var db = CreateDb();
        var client = await SeedClientAsync(db);

        var service = CreateService(db);
        var result = await service.CompleteProfileAsync(
            client.Id,
            BuildCnpjRequest(
                client.Email,
                "11222333000181",
                "SP",
                stateRegistration: "123456789",
                isStateRegistrationExempt: false));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            error => string.Equals(error.Field, "stateRegistration", StringComparison.OrdinalIgnoreCase) &&
                     error.Message.Contains("fiscal UF RJ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompleteProfile_WhenLookupIsUnavailable_UsesAddressStateForIeValidation()
    {
        await using var db = CreateDb();
        var client = await SeedClientAsync(db);

        var service = CreateService(db);
        var result = await service.CompleteProfileAsync(
            client.Id,
            BuildCnpjRequest(
                client.Email,
                "33444555000181",
                "MG",
                stateRegistration: "123456789",
                isStateRegistrationExempt: false));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Errors,
            error => string.Equals(error.Field, "stateRegistration", StringComparison.OrdinalIgnoreCase) &&
                     error.Message.Contains("fiscal UF MG", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RegisterPublic_WithOutsideSpCnpj_RequiresAcceptance_AndPersistsWhenAccepted()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var rejected = await service.RegisterPublicAsync(
            BuildPublicRegisterRequest("cliente.public001@example.test", "11222333000181", "RJ"));
        Assert.False(rejected.Succeeded);
        Assert.Contains(
            rejected.Errors,
            error => string.Equals(error.Field, "outOfSpCnpjWarningAccepted", StringComparison.OrdinalIgnoreCase));

        var accepted = await service.RegisterPublicAsync(
            BuildPublicRegisterRequest(
                "cliente.public002@example.test",
                "11222333000181",
                "RJ",
                outOfSpCnpjWarningAccepted: true));

        Assert.True(accepted.Succeeded);
        var stored = await db.Clients.SingleAsync(item => item.Email == "cliente.public002@example.test");
        Assert.Equal("RJ", stored.CnpjUf);
        Assert.True(stored.IsCnpjOutsideSp);
        Assert.True(stored.OutOfSpCnpjWarningAccepted);
        Assert.NotNull(stored.OutOfSpCnpjWarningAcceptedAt);
    }

    private static ClientService CreateService(AppDbContext db, IDocumentLookup? documentLookup = null)
    {
        return new ClientService(
            db,
            new FakeCepLookup(),
            documentLookup ?? new MockDocumentLookupService());
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Client> SeedClientAsync(AppDbContext db, Action<Client>? configure = null)
    {
        var tenantId = Guid.NewGuid().ToString("N");
        db.Tenants.Add(new Phub.Domain.Entities.Tenant
        {
            Id = tenantId,
            Name = "Tenant Teste",
            Slug = $"tenant-{tenantId[..6]}",
            Status = TenantStatus.Active
        });

        var client = new Client
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ProtheusCode = $"P-{tenantId[..6].ToUpperInvariant()}",
            AccountName = "Cliente Teste",
            Email = $"cliente.{tenantId[..6]}@example.test",
            PasswordHash = PasswordHasher.HashPassword("Password123!"),
            Status = ClientStatus.PendingProfile,
            MustChangePassword = false,
            ProtheusTag = ProtheusTag.Build(ProtheusPrefixes.Client, ProtheusOperationType.CREATE)
        };

        configure?.Invoke(client);
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    private static ClientUpdateRequest BuildCpfRequest(string email)
    {
        return new ClientUpdateRequest
        {
            PersonType = PersonType.CPF,
            LegalName = "Cliente PF Teste",
            TradeName = null,
            Document = "52998224725",
            StateRegistration = null,
            IsStateRegistrationExempt = false,
            OutOfSpCnpjWarningAccepted = false,
            Email = email,
            Whatsapp = "11999999999",
            ZipCode = "01311000",
            Street = "Av Paulista",
            Number = "1000",
            District = "Bela Vista",
            City = "Sao Paulo",
            State = "SP",
            Complement = "Conj 101",
            ResponsibleName = "Responsavel PF",
            ResponsibleDocument = "52998224725"
        };
    }

    private static ClientUpdateRequest BuildCnpjRequest(
        string email,
        string document,
        string state,
        bool outOfSpCnpjWarningAccepted = false,
        string stateRegistration = "ISENTO",
        bool isStateRegistrationExempt = true)
    {
        return new ClientUpdateRequest
        {
            PersonType = PersonType.CNPJ,
            LegalName = "Cliente PJ Teste LTDA",
            TradeName = "Cliente PJ Teste",
            Document = document,
            StateRegistration = stateRegistration,
            IsStateRegistrationExempt = isStateRegistrationExempt,
            OutOfSpCnpjWarningAccepted = outOfSpCnpjWarningAccepted,
            Email = email,
            Whatsapp = "11999999999",
            ZipCode = "01311000",
            Street = "Av Paulista",
            Number = "1000",
            District = "Bela Vista",
            City = state == "RJ" ? "Rio de Janeiro" : "Sao Paulo",
            State = state,
            Complement = "Conj 101",
            ResponsibleName = "Responsavel PJ",
            ResponsibleDocument = "52998224725"
        };
    }

    private static ClientPublicRegisterRequest BuildPublicRegisterRequest(
        string email,
        string document,
        string state,
        bool outOfSpCnpjWarningAccepted = false)
    {
        return new ClientPublicRegisterRequest
        {
            PersonType = PersonType.CNPJ,
            LegalName = "Cadastro Publico LTDA",
            TradeName = "Cadastro Publico",
            Document = document,
            StateRegistration = "ISENTO",
            IsStateRegistrationExempt = true,
            OutOfSpCnpjWarningAccepted = outOfSpCnpjWarningAccepted,
            Email = email,
            Whatsapp = "11999999999",
            ZipCode = "01311000",
            Street = "Av Paulista",
            Number = "1000",
            District = "Bela Vista",
            City = state == "RJ" ? "Rio de Janeiro" : "Sao Paulo",
            State = state,
            Complement = "Conj 101",
            ResponsibleName = "Responsavel Publico",
            ResponsibleDocument = "52998224725"
        };
    }

    private sealed class FakeCepLookup : ICepLookup
    {
        public Task<CepLookupResult> LookupAsync(string cep, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CepLookupResult(CepLookupStatus.Found, "Av Paulista", "Bela Vista", "Sao Paulo", "SP"));
        }
    }
}
