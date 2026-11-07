param(
    [string]$OutputPath = "artifacts\publish"
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$target = [System.IO.Path]::GetFullPath((Join-Path $workspace $OutputPath))
if (-not $target.StartsWith($workspace, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "发布目录必须位于工作区内：$target"
}

Push-Location (Join-Path $workspace "src\ProjectHub.Web")
try {
    pnpm install --frozen-lockfile
    pnpm typecheck
    pnpm build
}
finally {
    Pop-Location
}

dotnet test (Join-Path $workspace "ProjectHub.slnx") -c Release
dotnet publish (Join-Path $workspace "src\ProjectHub.Api\ProjectHub.Api.csproj") -c Release -o $target
Write-Host "发布完成：$target" -ForegroundColor Green
