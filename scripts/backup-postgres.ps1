param(
    [string]$OutputDirectory = "backups"
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$backupRoot = [System.IO.Path]::GetFullPath((Join-Path $workspace $OutputDirectory))
if (-not $backupRoot.StartsWith($workspace, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "备份目录必须位于工作区内：$backupRoot"
}

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupFile = Join-Path $backupRoot "projecthub-$timestamp.sql"
docker compose exec -T postgres pg_dump --clean --if-exists --no-owner -U projecthub projecthub | Out-File -FilePath $backupFile -Encoding utf8
Write-Host "数据库备份完成：$backupFile" -ForegroundColor Green
