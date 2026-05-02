using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Sabr.Domain.Enums;

namespace Sabr.Api.Tests.TestHost;

public static class TestJwtFactory
{
    private const string Issuer = "SABR3";
    private const string Audience = "SABR3";

    /// <summary>
    /// Chave de assinatura usada pelos testes.
    /// Deve ser injetada via ConfigureAppConfiguration nas factories para que a API
    /// valide tokens gerados aqui sem mismatch de assinatura.
    /// </summary>
    internal const string TestSigningKey = "sabr-tests-only-signing-key-not-used-in-production-xunit";

    private const string SigningKey = TestSigningKey;

    public static string CreateTenantClientToken(string tenantId, Guid clientId, Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("tenantId", tenantId),
            new Claim("clientId", clientId.ToString()),
            new Claim("scope", "tenant"),
            new Claim("accountType", AccountTypes.Client)
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string CreateAdminToken(Guid userId, string role = "Admin")
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("scope", "platform"),
            new Claim(ClaimTypes.Role, role)
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
