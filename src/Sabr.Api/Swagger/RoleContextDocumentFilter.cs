using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Sabr.Api.Swagger;

public sealed class RoleContextDocumentFilter : IDocumentFilter
{
    private const string AdminSystemUsersTag = "AdminSystemUsers";
    private const string AdminTenantUsersTag = "AdminTenantUsers";

    private const string AdminSystemUsersDescription =
        "Usuarios internos do sistema. Roles: Admin / Finance / SuperAdmin (Sistema).";

    private const string AdminTenantUsersDescription =
        "Equipe do cliente (tenant). Roles tecnicos: Admin / Finance / SuperAdmin. Na UI, SuperAdmin e exibido como Owner (Cliente).";

    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        swaggerDoc.Tags ??= new List<OpenApiTag>();

        UpsertTag(swaggerDoc.Tags, AdminSystemUsersTag, AdminSystemUsersDescription);
        UpsertTag(swaggerDoc.Tags, AdminTenantUsersTag, AdminTenantUsersDescription);
    }

    private static void UpsertTag(IList<OpenApiTag> tags, string name, string description)
    {
        var existing = tags.FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.Ordinal));
        if (existing == null)
        {
            tags.Add(new OpenApiTag
            {
                Name = name,
                Description = description
            });
            return;
        }

        existing.Description = description;
    }
}
