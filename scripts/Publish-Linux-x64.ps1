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

# 解析输出目录（默认直接发布到 publish/linux-x64）
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    if ($Daily -or -not [string]::IsNullOrWhiteSpace($Date)) {
        $targetDate = if (-not [string]::IsNullOrWhiteSpace($Date)) { $Date } else { Get-Date -Format 'yyyyMMdd' }
        $OutputDirectory = "publish/$targetDate/linux-x64"
    } else {
        $OutputDirectory = 'publish/linux-x64'
    }
}
$publishPath = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)

if (Test-Path -LiteralPath $publishPath) {
    Get-ChildItem -LiteralPath $publishPath -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
} else {
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

    # 针对 Linux 发布定制默认配置：启用模拟键盘输出，并将目标设为 gedit
    $settingsFile = Join-Path $publishPath 'appsettings.json'
    if (-not (Test-Path -LiteralPath $settingsFile)) {
        $sourceSettings = Join-Path $repoRoot 'src/ScanFlowOcr.App/appsettings.json'
        if (Test-Path -LiteralPath $sourceSettings) {
            Copy-Item -LiteralPath $sourceSettings -Destination $settingsFile -Force
        }
    }
    if (Test-Path -LiteralPath $settingsFile) {
        try {
            $jsonContent = Get-Content -LiteralPath $settingsFile -Raw -Encoding utf8 | ConvertFrom-Json
            if ($jsonContent.ScanFlowOcr) {
                $jsonContent.ScanFlowOcr.KeyboardEnabled = $true
                $jsonContent.ScanFlowOcr.KeyboardTargetProcess = "gedit"
                $jsonContent.ScanFlowOcr.MqttEnabled = $false
                $jsonContent.ScanFlowOcr.TcpEnabled = $false
                $newJson = $jsonContent | ConvertTo-Json -Depth 10
                [IO.File]::WriteAllText($settingsFile, $newJson, (New-Object System.Text.UTF8Encoding($false)))
                Write-Host ">>> 已将 Linux 默认配置定制为: 键盘模拟输出 -> gedit" -ForegroundColor Cyan
            }
        } catch {
            Write-Warning "定制 appsettings.json 失败: $_"
        }
    }

    # 自动生成 Linux 一键自赋权启动脚本 run.sh (严格采用 LF 换行符)
    $runShPath = Join-Path $publishPath 'run.sh'
    $runShContent = @'
#!/usr/bin/env bash
# ==============================================================================
# ScanFlow-OCR Linux 一键启动脚本
# 自动检测并赋予执行权限，设置动态链接库搜索路径后启动程序
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MAIN_BIN="${SCRIPT_DIR}/ScanFlowOcr.App"

if [ ! -f "${MAIN_BIN}" ]; then
    echo "错误: 未找到主程序文件: ${MAIN_BIN}" >&2
    exit 1
fi

# 确保主程序具备执行权限
if [ ! -x "${MAIN_BIN}" ]; then
    chmod +x "${MAIN_BIN}" 2>/dev/null || true
fi

# 配置原生动态链接库搜索路径（当前目录 + native/linux-x64）
export LD_LIBRARY_PATH="${SCRIPT_DIR}:${SCRIPT_DIR}/native/linux-x64:${LD_LIBRARY_PATH:-}"

echo "正在启动 ScanFlow-OCR 桌面端..."
exec "${MAIN_BIN}" "$@"
'@
    [IO.File]::WriteAllText($runShPath, ($runShContent -replace "\r\n", "`n"), (New-Object System.Text.UTF8Encoding($false)))

    # 自动生成权限初始化脚本 setup-permissions.sh
    $setupShPath = Join-Path $publishPath 'setup-permissions.sh'
    $setupShContent = @'
#!/usr/bin/env bash
# ==============================================================================
# ScanFlow-OCR Linux 权限初始化脚本
# 为当前目录所有可执行程序与 shell 脚本赋予执行权限 (+x)
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "正在为 ScanFlow-OCR 产物赋予执行权限..."
chmod +x "${SCRIPT_DIR}/ScanFlowOcr.App" 2>/dev/null || true
chmod +x "${SCRIPT_DIR}"/*.sh 2>/dev/null || true

# 确保原生 so 动态库具备读与执行权限
find "${SCRIPT_DIR}" -type f -name "*.so*" -exec chmod 755 {} + 2>/dev/null || true

echo "权限配置完成！您现在可以直接执行: ./run.sh 或 ./ScanFlowOcr.App"
'@
    [IO.File]::WriteAllText($setupShPath, ($setupShContent -replace "\r\n", "`n"), (New-Object System.Text.UTF8Encoding($false)))

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
    Write-Host "启动脚本 : run.sh（内置自动赋权与 LD_LIBRARY_PATH 配置）"
    Write-Host "赋权脚本 : setup-permissions.sh"
    Write-Host "启动方法 : 在 Linux 终端执行 bash ./run.sh"
    Write-Host "========================================================`n" -ForegroundColor Green
}
finally {
    Pop-Location
}
