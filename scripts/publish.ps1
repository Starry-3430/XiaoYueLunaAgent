#Requires -Version 7
[CmdletBinding()]
param(
    [ValidateSet('selfcontained', 'frameworkdependent', 'all')]
    [string]$Mode = 'all'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'Luna\Luna.csproj'

function Publish-And-Zip {
    param([string]$Profile, [string]$OutDir, [string]$ZipPath)

    Write-Host "==> 发布 $Profile" -ForegroundColor Cyan
    dotnet publish $proj -p:PublishProfile=$Profile --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败: $Profile" }

    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal

    $size = [math]::Round((Get-Item -LiteralPath $ZipPath).Length / 1MB, 1)
    Write-Host "==> 已生成 $ZipPath ($size MB)" -ForegroundColor Green
}

if ($Mode -in 'selfcontained', 'all') {
    Publish-And-Zip -Profile 'win-x64-selfcontained' `
        -OutDir (Join-Path $root 'publish\Luna-win-x64') `
        -ZipPath (Join-Path $root 'publish\Luna-win-x64.zip')
}

if ($Mode -in 'frameworkdependent', 'all') {
    Publish-And-Zip -Profile 'win-x64-frameworkdependent' `
        -OutDir (Join-Path $root 'publish\Luna-win-x64-fdd') `
        -ZipPath (Join-Path $root 'publish\Luna-win-x64-fdd.zip')
}
