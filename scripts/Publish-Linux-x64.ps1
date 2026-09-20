#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # 输出目录；若未指定则默认生成 publish/ScanFlowOcr-linux-x64-yyyyMMdd-HHmmss
    [string]$OutputDirectory,

    # 指定发布日期（如 20260920）
    [string]$Date,
    [switch]$Daily,

    [switch]$Force,
    [switch]$Overwrite,

    # 单文件打包（默认开启）
    [bool]$SingleFile = $true,

    # 自包含模式（Linux 默认开启自包含，目标机器无需预装 .NET 运行时）
    [bool]$SelfContained = $true,

    # 实验性 Native AOT 模式（在 Linux 原生宿主机上有效，跨平台交叉编译需额外配置 clang/glibc）
    [switch]$Aot
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src/ScanFlowOcr.App/ScanFlowOcr.App.csproj'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "未找到工程文件：$projectPath"
}

# 解析输出目录（默认直接发布到仓库根目录 publish）
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    if ($Daily -or -not [string]::IsNullOrWhiteSpace($Date)) {
        $targetDate = if (-not [string]::IsNullOrWhiteSpace($Date)) { $Date } else { Get-Date -Format 'yyyyMMdd' }
        $OutputDirectory = "publish/$targetDate"
    } else {
        $OutputDirectory = 'publish'
    }
}
$publishPath = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)

if (-not (Test-Path -LiteralPath $publishPath)) {
    New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
}

$publishArgs = @(
    'publish', $projectPath,
    '-c', $Configuration,
    '-r', 'linux-x64',
    '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=$($SingleFile.ToString().ToLowerInvariant())",
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:TreatWarningsAsErrors=false',
    '-o', $publishPath
)

if ($Aot.IsPresent) {
    Write-Host ">>> 模式: Linux x64 Native AOT（跨平台交叉编译要求宿主环境具备 Linux 工具链或在 Linux 本地执行）..." -ForegroundColor Cyan
    $publishArgs += '-p:PublishAot=true'
} else {
    Write-Host ">>> 模式: Linux x64 自包含单文件发布 (Self-Contained SingleFile)..." -ForegroundColor Cyan
}

if (-not $PSCmdlet.ShouldProcess($publishPath, '执行 ScanFlow-OCR Linux x64 发布')) {
    return
}

Push-Location $repoRoot
try {
    Write-Host "正在执行: dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败（退出码 $LASTEXITCODE）。产物目录：$publishPath"
    }

    # 清理符号文件
    Get-ChildItem -LiteralPath $publishPath -Filter '*.pdb' -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # 补充确保原生 libturbojpeg.so 随包交付
    $nativeSrc = Join-Path $repoRoot 'native/linux-x64/libturbojpeg.so'
    $nativeDestDir = Join-Path $publishPath 'native/linux-x64'
    $nativeDestFile = Join-Path $nativeDestDir 'libturbojpeg.so'
    if ((Test-Path -LiteralPath $nativeSrc) -and -not (Test-Path -LiteralPath $nativeDestFile)) {
        New-Item -ItemType Directory -Path $nativeDestDir -Force | Out-Null
        Copy-Item -LiteralPath $nativeSrc -Destination $nativeDestFile -Force
    }

    # 校验产物
    $mainBinary = Join-Path $publishPath 'ScanFlowOcr.App'
    if (-not (Test-Path -LiteralPath $mainBinary)) {
        throw "发布产物缺失：ScanFlowOcr.App"
    }

    $binSize = [math]::Round((Get-Item -LiteralPath $mainBinary).Length / 1MB, 2)
    Write-Host "`n========================================================" -ForegroundColor Green
    Write-Host "ScanFlow-OCR Linux x64 发布成功！" -ForegroundColor Green
    Write-Host "交付目录 : $publishPath"
    Write-Host "主程序   : ScanFlowOcr.App (${binSize} MB)"
    Write-Host "部署说明 : Linux 部署时请执行 chmod +x ./ScanFlowOcr.App 赋予执行权限"
    Write-Host "========================================================`n" -ForegroundColor Green
}
finally {
    Pop-Location
}
