param(
  [Parameter(Mandatory = $true)]
  [string]$BucketName,
  [Parameter(Mandatory = $true)]
  [string]$LockTableName,
  [string]$Region = 'sa-east-1'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command aws -ErrorAction SilentlyContinue)) {
  throw 'AWS CLI não encontrado.'
}

aws sts get-caller-identity | Out-Null

# Cria bucket se não existir
$bucketExists = $false
try {
  aws s3api head-bucket --bucket $BucketName 2>$null | Out-Null
  $bucketExists = $true
}
catch {
  $bucketExists = $false
}

if (-not $bucketExists) {
  if ($Region -eq 'us-east-1') {
    aws s3api create-bucket --bucket $BucketName | Out-Null
  }
  else {
    aws s3api create-bucket --bucket $BucketName --create-bucket-configuration LocationConstraint=$Region | Out-Null
  }
}

aws s3api put-bucket-versioning --bucket $BucketName --versioning-configuration Status=Enabled | Out-Null
aws s3api put-bucket-encryption --bucket $BucketName --server-side-encryption-configuration '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}' | Out-Null

# DynamoDB lock table
$tableExists = $false
try {
  aws dynamodb describe-table --table-name $LockTableName --region $Region 2>$null | Out-Null
  $tableExists = $true
}
catch {
  $tableExists = $false
}

if (-not $tableExists) {
  aws dynamodb create-table --table-name $LockTableName --attribute-definitions AttributeName=LockID,AttributeType=S --key-schema AttributeName=LockID,KeyType=HASH --billing-mode PAY_PER_REQUEST --region $Region | Out-Null
  aws dynamodb wait table-exists --table-name $LockTableName --region $Region
}

Write-Host "Terraform backend pronto: s3://$BucketName + table $LockTableName" -ForegroundColor Green
