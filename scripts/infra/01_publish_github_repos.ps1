param(
  [Parameter(Mandatory = $true)]
  [string]$GitHubOwner,
  [string]$DotnetRepoName = 'sabr-dotnet',
  [string]$FrontendRepoName = 'sabr-frontend',
  [switch]$Public
)

$ErrorActionPreference = 'Stop'
$root = 'C:\Users\euron\Documents\Projeto SABR 3.0'
$dotnetPath = Join-Path $root 'sabr-dotnet'
$frontendPath = Join-Path $root 'sabr-frontend'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
  throw 'GitHub CLI (gh) não encontrado.'
}

try {
  gh auth status | Out-Null
}
catch {
  throw 'GitHub CLI não autenticado. Execute: gh auth login --web'
}

$visibility = if ($Public) { '--public' } else { '--private' }

function Publish-Repo {
  param(
    [string]$RepoPath,
    [string]$RepoFullName
  )

  Push-Location $RepoPath
  try {
    $currentRemote = ''
    try { $currentRemote = (git remote get-url origin) } catch { $currentRemote = '' }

    if (-not $currentRemote) {
      gh repo create $RepoFullName $visibility --source . --remote origin
    }

    git push -u origin main
    git push -u origin develop
  }
  finally {
    Pop-Location
  }
}

Publish-Repo -RepoPath $dotnetPath -RepoFullName "$GitHubOwner/$DotnetRepoName"
Publish-Repo -RepoPath $frontendPath -RepoFullName "$GitHubOwner/$FrontendRepoName"

Write-Host 'Repos publicados com sucesso.' -ForegroundColor Green
