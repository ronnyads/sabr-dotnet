using Phub.Application.Abstractions;
using Phub.Application.Exceptions;
using Phub.Application.Models;
using Phub.Application.Validation;

namespace Phub.Infrastructure.Services;

public sealed class MockDocumentLookupService : IDocumentLookup
{
    // Exemplos mockados
    private static readonly Dictionary<string, DocumentLookupResult> Fixtures = new(StringComparer.Ordinal)
    {
        {
            "60355549000120",
            new DocumentLookupResult
            {
                PersonType = "pj",
                LegalName = "Empresa Mock de Teste LTDA",
                TradeName = "Mock Teste",
                StateRegistration = "123456789",
                IsStateRegistrationExempt = false,
                Address = new DocumentAddress
                {
                    ZipCode = "01311000",
                    Street = "Av Paulista",
                    Number = "1000",
                    District = "Bela Vista",
                    City = "Sao Paulo",
                    State = "SP",
                    Complement = "Conj 101"
                }
            }
        },
        {
            "11222333000181",
            new DocumentLookupResult
            {
                PersonType = "pj",
                LegalName = "Empresa Mock Fora de SP LTDA",
                TradeName = "Mock Fora SP",
                StateRegistration = "123456789",
                IsStateRegistrationExempt = false,
                Address = new DocumentAddress
                {
                    ZipCode = "20040002",
                    Street = "Rua da Quitanda",
                    Number = "200",
                    District = "Centro",
                    City = "Rio de Janeiro",
                    State = "RJ",
                    Complement = null
                }
            }
        },
        {
            "12345678909",
            new DocumentLookupResult
            {
                PersonType = "pf",
                LegalName = "Fulano Mock da Silva",
                TradeName = null,
                IsStateRegistrationExempt = true,
                StateRegistration = null,
                Address = new DocumentAddress
                {
                    ZipCode = "20040002",
                    Street = "Rua da Quitanda",
                    Number = "200",
                    District = "Centro",
                    City = "Rio de Janeiro",
                    State = "RJ",
                    Complement = null
                }
            }
        }
    };

    public Task<DocumentLookupResult?> LookupAsync(string documentDigits, CancellationToken cancellationToken = default)
    {
        var digits = BrazilValidators.OnlyDigits(documentDigits);
        if (digits == "33444555000181")
        {
            throw new ExternalServiceUnavailableException("Mock lookup unavailable");
        }

        if (Fixtures.TryGetValue(digits, out var result))
        {
            return Task.FromResult<DocumentLookupResult?>(result);
        }

        return Task.FromResult<DocumentLookupResult?>(null);
    }
}
