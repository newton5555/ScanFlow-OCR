#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # 输出目录；若未指定则默认生成 publish/ScanFlowOcr-win-x64-yyyyMMdd-HHmmss
    [string]$OutputDirectory,

    # 指定发布日期目录（如 20260920），生成形如 publish/ScanFlowOcr-win-x64-yyyyMMdd
    [string]$Date,

    # 按天命名的快捷开关
    [switch]$Daily,

    # 覆盖已存在的发布目录
    [switch]$Force,
    [switch]$Overwrite,

    # 单 EXE 打包（默认开启，所有托管 DLL 打包入单一可执行程序）
    [bool]$SingleFile = $true,

    # 是否自包含（默认关闭即依赖目标机器 .NET 10 运行时，生成极致轻量的依赖框架单 EXE；开启后为自包含）
    [switch]$SelfContained,

    # 实验性 Native AOT 编译开关（注意：Native AOT 强制要求 SelfContained）
    [switch]$Aot
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src/ScanFlowOcr.App/ScanFlowOcr.App.csproj'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "未找到工程文件：$projectPath"
}

# 解析输出目录（默认直接发布到 publish/win-x64）
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    if ($Daily -or -not [string]::IsNullOrWhiteSpace($Date)) {
        $targetDate = if (-not [string]::IsNullOrWhiteSpace($Date)) { $Date } else { Get-Date -Format 'yyyyMMdd' }
        $OutputDirectory = "publish/$targetDate/win-x64"
    } else {
        $OutputDirectory = 'publish/win-x64'
    }
}
$publishPath = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)

if (Test-Path -LiteralPath $publishPath) {
    Get-ChildItem -LiteralPath $publishPath -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
} else {
    New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
}

# 模式判断与参数拼装
$isSelfContained = $SelfContained.IsPresent -or $Aot.IsPresent
$publishArgs = @(
    'publish', $projectPath,
    '-c', $Configuration,
    '-r', 'win-x64',
    '--self-contained', $isSelfContained.ToString().ToLowerInvariant(),
    "-p:PublishSingleFile=$($SingleFile.ToString().ToLowerInvariant())",
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:TreatWarningsAsErrors=false',
    '-o', $publishPath
)

if ($Aot.IsPresent) {
    Write-Host ">>> 模式: Windows x64 Native AOT（需本机安装 MSVC C++ x64 编译工具链）..." -ForegroundColor Cyan
    $publishArgs += '-p:PublishAot=true'
} else {
    if ($isSelfContained) {
        Write-Host ">>> 模式: Windows x64 自包含单文件发布 (Self-Contained)..." -ForegroundColor Cyan
    } else {
        Write-Host ">>> 模式: Windows x64 框架依赖单 EXE 发布 (Framework-Dependent，体积小巧)..." -ForegroundColor Cyan
    }
}

if (-not $PSCmdlet.ShouldProcess($publishPath, '执行 ScanFlow-OCR 发布')) {
    return
}

Push-Location $repoRoot
try {
    Write-Host "正在执行: dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败（退出码 $LASTEXITCODE）。产物目录：$publishPath"
    }

    # 彻底剔除发布目录中的 .pdb 调试符号文件，减少近 100MB 冗余
    Get-ChildItem -LiteralPath $publishPath -Filter '*.pdb' -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # 补充确保原生 turbojpeg.dll 随包交付
    $nativeSrc = Join-Path $repoRoot 'native/win-x64/turbojpeg.dll'
    $nativeDestDir = Join-Path $publishPath 'native/win-x64'
    $nativeDestFile = Join-Path $nativeDestDir 'turbojpeg.dll'
    if ((Test-Path -LiteralPath $nativeSrc) -and -not (Test-Path -LiteralPath $nativeDestFile)) {
        New-Item -ItemType Directory -Path $nativeDestDir -Force | Out-Null
        Copy-Item -LiteralPath $nativeSrc -Destination $nativeDestFile -Force
    }

    # 校验产物
    $mainExe = Join-Path $publishPath 'ScanFlowOcr.App.exe'
    if (-not (Test-Path -LiteralPath $mainExe)) {
        throw "发布产物缺失：ScanFlowOcr.App.exe"
    }

    $exeSize = [math]::Round((Get-Item -LiteralPath $mainExe).Length / 1MB, 2)
    Write-Host "`n========================================================" -ForegroundColor Green
    Write-Host "ScanFlow-OCR Windows x64 发布成功！" -ForegroundColor Green
    Write-Host "交付目录 : $publishPath"
    Write-Host "主可执行 : ScanFlowOcr.App.exe (${exeSize} MB)"
    Write-Host "运行要求 : $(if ($isSelfContained) { '已自包含完整运行时，可在任意 Windows 10/11 x64 独立运行' } else { '需目标机器安装 .NET 10 Desktop Runtime x64' })"
    Write-Host "========================================================`n" -ForegroundColor Green
}
finally {
    Pop-Location
}
