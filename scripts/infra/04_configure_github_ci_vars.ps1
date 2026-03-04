param(
  [Parameter(Mandatory = $true)]
  [string]$GitHubOwner,
  [string]$BackendRepo = 'sabr-dotnet',
  [string]$FrontendRepo = 'sabr-frontend',
  [string]$TerraformPath = 'C:\Users\euron\Documents\Projeto SABR 3.0\sabr-dotnet\infra\terraform'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
  throw 'GitHub CLI não encontrado.'
}

Push-Location $TerraformPath
try {
  $tf = terraform output -json | ConvertFrom-Json
}
finally {
  Pop-Location
}

$backendRepoFull = "$GitHubOwner/$BackendRepo"
$frontendRepoFull = "$GitHubOwner/$FrontendRepo"

$cluster = $tf.ecs_cluster_name.value
$ecrDev = $tf.ecr_repository_names.value.dev
$ecrProd = $tf.ecr_repository_names.value.prod
$svcDev = $tf.ecs_service_names.value.dev
$svcProd = $tf.ecs_service_names.value.prod

$backendRole = $tf.github_backend_role_arn.value
$frontendRole = $tf.github_frontend_role_arn.value

$apiDev = $tf.api_urls.value.dev
$apiProd = $tf.api_urls.value.prod

$bucketClientDev = $tf.s3_bucket_names.value.app_dev
$bucketAdminDev = $tf.s3_bucket_names.value.admin_dev
$bucketClientProd = $tf.s3_bucket_names.value.app_prod
$bucketAdminProd = $tf.s3_bucket_names.value.admin_prod

$cfClientDev = $tf.cloudfront_distribution_ids.value.app_dev
$cfAdminDev = $tf.cloudfront_distribution_ids.value.admin_dev
$cfClientProd = $tf.cloudfront_distribution_ids.value.app_prod
$cfAdminProd = $tf.cloudfront_distribution_ids.value.admin_prod

# Backend repo variables
$backendVars = @{
  AWS_REGION = 'sa-east-1'
  ECS_CLUSTER_NAME = $cluster
  ECS_SERVICE_DEV = $svcDev
  ECS_SERVICE_PROD = $svcProd
  ECR_REPOSITORY_DEV = $ecrDev
  ECR_REPOSITORY_PROD = $ecrProd
  ECS_CONTAINER_NAME = 'sabr-api'
}

foreach ($key in $backendVars.Keys) {
  gh variable set $key --repo $backendRepoFull --body $backendVars[$key] | Out-Null
}

if ($backendRole) {
  gh secret set AWS_ROLE_ARN_DEV --repo $backendRepoFull --body $backendRole | Out-Null
  gh secret set AWS_ROLE_ARN_PROD --repo $backendRepoFull --body $backendRole | Out-Null
}

# Frontend repo variables
$frontendVars = @{
  AWS_REGION = 'sa-east-1'
  S3_BUCKET_CLIENT_DEV = $bucketClientDev
  S3_BUCKET_ADMIN_DEV = $bucketAdminDev
  S3_BUCKET_CLIENT_PROD = $bucketClientProd
  S3_BUCKET_ADMIN_PROD = $bucketAdminProd
  CF_DIST_CLIENT_DEV = $cfClientDev
  CF_DIST_ADMIN_DEV = $cfAdminDev
  CF_DIST_CLIENT_PROD = $cfClientProd
  CF_DIST_ADMIN_PROD = $cfAdminProd
  API_BASE_URL_DEV = $apiDev
  API_BASE_URL_PROD = $apiProd
}

foreach ($key in $frontendVars.Keys) {
  gh variable set $key --repo $frontendRepoFull --body $frontendVars[$key] | Out-Null
}

if ($frontendRole) {
  gh secret set AWS_ROLE_ARN_DEV --repo $frontendRepoFull --body $frontendRole | Out-Null
  gh secret set AWS_ROLE_ARN_PROD --repo $frontendRepoFull --body $frontendRole | Out-Null
}

Write-Host 'GitHub variables/secrets configurados com sucesso.' -ForegroundColor Green
