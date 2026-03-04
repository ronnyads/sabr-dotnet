param(
  [Parameter(Mandatory = $true)]
  [string]$RootDomain
)

$ErrorActionPreference = 'Stop'

$targets = @(
  "https://api-dev.$RootDomain/health/live",
  "https://api-dev.$RootDomain/health/ready",
  "https://api.$RootDomain/health/live",
  "https://api.$RootDomain/health/ready",
  "https://app-dev.$RootDomain",
  "https://admin-dev.$RootDomain",
  "https://app.$RootDomain",
  "https://admin.$RootDomain"
)

foreach ($url in $targets) {
  try {
    $resp = Invoke-WebRequest -Uri $url -Method GET -TimeoutSec 30
    Write-Host "[OK] $($resp.StatusCode) $url" -ForegroundColor Green
  }
  catch {
    Write-Host "[FAIL] $url -> $($_.Exception.Message)" -ForegroundColor Red
    throw
  }
}

Write-Host 'Smoke test finalizado com sucesso.' -ForegroundColor Green
