param(
  [Parameter(Mandatory = $true)]
  [string]$GitHubOwner,
  [string]$BackendRepo = 'sabr-dotnet',
  [string]$FrontendRepo = 'sabr-frontend'
)

$ErrorActionPreference = 'Stop'

function Configure-RepoGovernance {
  param([string]$Repo)

  $repoFull = "$GitHubOwner/$Repo"

  # Environments
  gh api -X PUT "repos/$repoFull/environments/development" | Out-Null
  gh api -X PUT "repos/$repoFull/environments/production" | Out-Null

  # Branch protection for main
  $protectionPayload = @{
    required_status_checks = @{
      strict   = $true
      contexts = @()
    }
    enforce_admins = $true
    required_pull_request_reviews = @{
      dismissal_restrictions            = @{}
      dismiss_stale_reviews             = $true
      require_code_owner_reviews        = $false
      required_approving_review_count   = 1
      require_last_push_approval        = $false
    }
    restrictions = $null
    required_linear_history = $false
    allow_force_pushes = $false
    allow_deletions = $false
    block_creations = $false
    required_conversation_resolution = $true
    lock_branch = $false
    allow_fork_syncing = $true
  } | ConvertTo-Json -Depth 10

  $tmpFile = [System.IO.Path]::GetTempFileName()
  try {
    Set-Content -Path $tmpFile -Value $protectionPayload -Encoding UTF8
    gh api -X PUT "repos/$repoFull/branches/main/protection" --input $tmpFile | Out-Null
  }
  finally {
    Remove-Item $tmpFile -ErrorAction SilentlyContinue
  }
}

Configure-RepoGovernance -Repo $BackendRepo
Configure-RepoGovernance -Repo $FrontendRepo

Write-Host 'Governança GitHub aplicada com sucesso.' -ForegroundColor Green
