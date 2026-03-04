param(
  [Parameter(Mandatory = $true)]
  [string]$StateBucket,
  [Parameter(Mandatory = $true)]
  [string]$LockTable,
  [Parameter(Mandatory = $true)]
  [string]$RootDomain,
  [Parameter(Mandatory = $true)]
  [string]$GitHubBackendRepo,
  [Parameter(Mandatory = $true)]
  [string]$GitHubFrontendRepo,
  [string]$Region = 'sa-east-1',
  [string]$Route53ZoneId = '',
  [switch]$AutoApprove
)

$ErrorActionPreference = 'Stop'
$tfPath = 'C:\Users\euron\Documents\Projeto SABR 3.0\sabr-dotnet\infra\terraform'

if (-not (Get-Command terraform -ErrorAction SilentlyContinue)) {
  throw 'Terraform não encontrado.'
}

if (-not (Get-Command aws -ErrorAction SilentlyContinue)) {
  throw 'AWS CLI não encontrado.'
}

aws sts get-caller-identity | Out-Null

Push-Location $tfPath
try {
  @"
terraform {
  backend "s3" {
    bucket         = "$StateBucket"
    key            = "sabr/platform/terraform.tfstate"
    region         = "$Region"
    dynamodb_table = "$LockTable"
    encrypt        = true
  }
}
"@ | Set-Content -Path backend.tf -Encoding UTF8

  @"
project_name = "sabr"
aws_region   = "$Region"
root_domain  = "$RootDomain"
route53_zone_id = "$Route53ZoneId"

nat_gateway_mode = "single"

github_backend_repository  = "$GitHubBackendRepo"
github_frontend_repository = "$GitHubFrontendRepo"
github_infra_repository    = "$GitHubBackendRepo"

enable_oidc_roles = true
"@ | Set-Content -Path terraform.tfvars -Encoding UTF8

  terraform init -reconfigure
  terraform validate

  $approve = if ($AutoApprove) { '-auto-approve' } else { '' }

  terraform plan -var-file=environments/dev.tfvars
  if ($AutoApprove) { terraform apply -auto-approve -var-file=environments/dev.tfvars } else { terraform apply -var-file=environments/dev.tfvars }

  terraform plan -var-file=environments/prod.tfvars
  if ($AutoApprove) { terraform apply -auto-approve -var-file=environments/prod.tfvars } else { terraform apply -var-file=environments/prod.tfvars }

  terraform output
}
finally {
  Pop-Location
}
