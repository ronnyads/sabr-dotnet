using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Brazil.Data;

namespace Sabr.Application.Validation;

public static class BrazilValidators
{
    private static readonly EmailAddressAttribute EmailValidator = new();
    private static readonly Regex NonDigits = new(@"\D+", RegexOptions.Compiled);

    public static string OnlyDigits(string value)
    {
        return NonDigits.Replace(value ?? string.Empty, string.Empty);
    }

    public static bool IsValidEmail(string value) => EmailValidator.IsValid(value);

    public static bool IsValidCpf(string value)
    {
        var digits = OnlyDigits(value);
        return !string.IsNullOrWhiteSpace(digits) && Cpf.Validate(digits);
    }

    public static bool IsValidCnpj(string value)
    {
        var digits = OnlyDigits(value);
        return !string.IsNullOrWhiteSpace(digits) && Cnpj.Validate(digits);
    }

    public static bool IsValidInscricaoEstadual(string? value, string uf, bool isExempt)
    {
        if (isExempt) return true;
        if (string.Equals(value?.Trim(), "ISENTO", StringComparison.OrdinalIgnoreCase)) return true;
        var digits = OnlyDigits(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(digits)) return false;
        return InscricaoEstadual.Validate(digits, uf, isExempt);
    }

    public static bool IsValidResponsibleDocument(string value)
    {
        // Responsável: exigir CPF válido
        return IsValidCpf(value);
    }

    public static bool IsValidWhatsapp(string value)
    {
        var digits = OnlyDigits(value);
        if (string.IsNullOrWhiteSpace(digits)) return false;
        if (digits.StartsWith("55"))
        {
            return digits.Length == 12 || digits.Length == 13;
        }
        return digits.Length == 10 || digits.Length == 11;
    }

    public static bool IsValidCep(string value)
    {
        var digits = OnlyDigits(value);
        return digits.Length == 8;
    }

    public static bool IsValidUF(string value)
    {
        var uf = (value ?? string.Empty).Trim().ToUpperInvariant();
        return BrazilStates.All.Contains(uf);
    }
}
