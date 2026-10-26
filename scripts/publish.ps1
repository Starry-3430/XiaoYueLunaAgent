#Requires -Version 7
[CmdletBinding()]
param(
    [ValidateSet('selfcontained', 'frameworkdependent', 'all')]
    [string]$Mode = 'all'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'Luna\Luna.csproj'
$complianceFiles = @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md')
$licenseDirSource = Join-Path $root 'licenses'

function Copy-ComplianceFiles {
    param([string]$OutDir)

    foreach ($file in $complianceFiles) {
        $source = Join-Path $root $file
        if (-not (Test-Path -LiteralPath $source)) { throw "缺少合规文件: $source" }
        Copy-Item -LiteralPath $source -Destination $OutDir -Force
    }

    if (-not (Test-Path -LiteralPath $licenseDirSource)) { throw "缺少许可证目录: $licenseDirSource" }
    $licenseFiles = @(Get-ChildItem -LiteralPath $licenseDirSource -Filter *.txt -File)
    if ($licenseFiles.Count -eq 0) { throw "许可证目录中没有可复制的文件: $licenseDirSource" }

    $licenseDir = Join-Path $OutDir 'licenses'
    New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
    foreach ($file in $licenseFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination $licenseDir -Force
    }
}

function Publish-And-Zip {
    param([string]$Profile, [string]$OutDir, [string]$ZipPath)

    Write-Host "==> 发布 $Profile" -ForegroundColor Cyan
    dotnet publish $proj -p:PublishProfile=$Profile --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败: $Profile" }

    Copy-ComplianceFiles -OutDir $OutDir

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
