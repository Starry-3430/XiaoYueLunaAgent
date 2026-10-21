#Requires -Version 7
[CmdletBinding()]
param(
    [string]$IsccPath,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'Luna\Luna.csproj'
$iss = Join-Path $root 'installer\Luna.iss'
$publishScript = Join-Path $root 'scripts\publish.ps1'
$publishDir = Join-Path $root 'publish\Luna-win-x64'

# 版本号以 Luna.csproj 的 <Version> 为唯一来源。
$version = ([xml](Get-Content -LiteralPath $proj -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Luna.csproj 中未找到 <Version>' }

if (-not $SkipPublish) {
    Write-Host "==> 生成自包含发布目录（版本 $version）" -ForegroundColor Cyan
    & $publishScript -Mode selfcontained
    if (-not $?) { throw 'publish.ps1 执行失败' }
}

$exe = Join-Path $publishDir 'Luna.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "未找到发布文件：$exe。请先运行 scripts\publish.ps1 -Mode selfcontained"
}

if (-not $IsccPath) {
    $candidates = @()
    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    }
    if ($env:ProgramFiles) {
        $candidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
    }
    if ($env:LOCALAPPDATA) {
        $candidates += Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    }

    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) {
    throw '未找到 ISCC.exe。请安装 Inno Setup 6，或通过 -IsccPath 指定路径。'
}

Write-Host "==> 编译安装包（$IsccPath）" -ForegroundColor Cyan
& $IsccPath "/DMyAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败，退出码 $LASTEXITCODE" }

$setup = Join-Path $root "publish\installer\Luna-v$version-Setup.exe"
if (-not (Test-Path -LiteralPath $setup)) {
    throw "未生成安装包：$setup"
}

$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$setupName = [System.IO.Path]::GetFileName($setup)
"$hash  $setupName" | Set-Content -LiteralPath "$setup.sha256" -Encoding ascii

$size = [math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 1)
Write-Host "==> 已生成 $setup ($size MB)" -ForegroundColor Green
Write-Host "==> SHA256: $hash" -ForegroundColor Green
