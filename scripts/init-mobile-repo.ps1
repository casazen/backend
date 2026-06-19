#Requires -Version 7.0
<#
.SYNOPSIS
  Initialize casazen/mobile repo and push to GitHub (#287).

.EXAMPLE
  .\scripts\init-mobile-repo.ps1 -GitHubOrg casazen
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$GitHubOrg
)

$ErrorActionPreference = 'Stop'
$MobileRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'mobile')

Push-Location $MobileRoot
try {
    if (-not (Test-Path '.git')) {
        git init
        git branch -M main
    }

    git add .
    git status

    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. git commit -m 'feat(mobile): Expo host app scaffold (#287)'"
    Write-Host "  2. gh repo create $GitHubOrg/mobile --private --source=. --push"
    Write-Host "  3. Set EXPO_PUBLIC_API_URL and Auth0 env in EAS"
}
finally {
    Pop-Location
}
