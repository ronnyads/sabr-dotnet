project_name    = "sabr"
aws_region      = "sa-east-1"
root_domain     = "marketplaceonline.site"
route53_zone_id = "Z0443643LR32IHPS3A4T"

# CloudFront bloqueado na conta AWS (aguardando verificação de suporte)
# Quando liberar: mudar para false e rodar terraform apply
skip_cloudfront = true

# Cloudflare Pages – preencher após criar o projeto em pages.cloudflare.com
# Ex: cloudflare_pages_project = "sabr-frontend.pages.dev"
cloudflare_pages_project = ""

nat_gateway_mode = "single"

db_backup_retention_dev  = 0
db_backup_retention_prod = 0   # Free Tier limit — reativar após upgrade da conta

github_backend_repository  = "ronnyads/sabr-dotnet"
github_frontend_repository = "ronnyads/sabr-frontend"
github_infra_repository    = "ronnyads/sabr-dotnet"

enable_oidc_roles = true
