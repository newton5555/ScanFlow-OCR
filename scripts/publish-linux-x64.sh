#!/usr/bin/env bash
# ==============================================================================
# ScanFlow-OCR Linux x64 发布脚本 (Native Linux Shell)
# 默认模式：Linux x64 自包含单文件发布 (Self-Contained Single-File)
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROJECT_PATH="${REPO_ROOT}/src/ScanFlowOcr.App/ScanFlowOcr.App.csproj"

CONFIGURATION="Release"
SELF_CONTAINED="true"
SINGLE_FILE="true"
USE_AOT="false"
OUTPUT_DIR=""

print_usage() {
    echo "用法: $0 [选项]"
    echo "选项:"
    echo "  -c, --configuration <Release|Debug>  编译配置 (默认: Release)"
    echo "  -o, --output <dir>                   输出目录 (默认: publish/ScanFlowOcr-linux-x64-时间戳)"
    echo "  --aot                                启用 Native AOT 原生编译 (需 clang/zlib/glibc)"
    echo "  --framework-dependent                改用依赖框架模式 (默认是自包含)"
    echo "  -h, --help                           显示此帮助信息"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        -o|--output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --aot)
            USE_AOT="true"
            SELF_CONTAINED="true"
            shift
            ;;
        --framework-dependent)
            SELF_CONTAINED="false"
            shift
            ;;
        -h|--help)
            print_usage
            exit 0
            ;;
        *)
            echo "未知参数: $1"
            print_usage
            exit 1
            ;;
    esac
done

if [[ -z "${OUTPUT_DIR}" ]]; then
    OUTPUT_DIR="${REPO_ROOT}/publish/linux-x64"
fi

mkdir -p "${OUTPUT_DIR}"

PUBLISH_ARGS=(
    publish "${PROJECT_PATH}"
    -c "${CONFIGURATION}"
    -r linux-x64
    --self-contained "${SELF_CONTAINED}"
    "-p:PublishSingleFile=${SINGLE_FILE}"
    "-p:DebugType=None"
    "-p:DebugSymbols=false"
    "-p:TreatWarningsAsErrors=false"
    -o "${OUTPUT_DIR}"
)

if [[ "${USE_AOT}" == "true" ]]; then
    echo ">>> 模式: Linux x64 Native AOT 原生编译..."
    PUBLISH_ARGS+=("-p:PublishAot=true")
else
    echo ">>> 模式: Linux x64 自包含单文件发布 (SelfContained=${SELF_CONTAINED})..."
fi

echo "正在执行: dotnet ${PUBLISH_ARGS[*]}"
dotnet "${PUBLISH_ARGS[@]}"

# 清理无用的调试符号
find "${OUTPUT_DIR}" -type f -name "*.pdb" -delete 2>/dev/null || true

# 确保原生 libturbojpeg.so 随包交付
NATIVE_SRC="${REPO_ROOT}/native/linux-x64/libturbojpeg.so"
NATIVE_DEST_DIR="${OUTPUT_DIR}/native/linux-x64"
if [[ -f "${NATIVE_SRC}" ]] && [[ ! -f "${NATIVE_DEST_DIR}/libturbojpeg.so" ]]; then
    mkdir -p "${NATIVE_DEST_DIR}"
    cp -f "${NATIVE_SRC}" "${NATIVE_DEST_DIR}/libturbojpeg.so"
fi

# 自动生成 Linux 一键自赋权启动脚本 run.sh
cat > "${OUTPUT_DIR}/run.sh" << 'EOF'
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MAIN_BIN="${SCRIPT_DIR}/ScanFlowOcr.App"

if [ ! -f "${MAIN_BIN}" ]; then
    echo "错误: 未找到主程序文件: ${MAIN_BIN}" >&2
    exit 1
fi

if [ ! -x "${MAIN_BIN}" ]; then
    chmod +x "${MAIN_BIN}" 2>/dev/null || true
fi

export LD_LIBRARY_PATH="${SCRIPT_DIR}:${SCRIPT_DIR}/native/linux-x64:${LD_LIBRARY_PATH:-}"

echo "正在启动 ScanFlow-OCR 桌面端..."
exec "${MAIN_BIN}" "$@"
EOF
chmod +x "${OUTPUT_DIR}/run.sh"

# 自动生成权限初始化脚本 setup-permissions.sh
cat > "${OUTPUT_DIR}/setup-permissions.sh" << 'EOF'
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "正在为 ScanFlow-OCR 产物赋予执行权限..."
chmod +x "${SCRIPT_DIR}/ScanFlowOcr.App" 2>/dev/null || true
chmod +x "${SCRIPT_DIR}"/*.sh 2>/dev/null || true

find "${SCRIPT_DIR}" -type f -name "*.so*" -exec chmod 755 {} + 2>/dev/null || true

echo "权限配置完成！您现在可以直接执行: ./run.sh 或 ./ScanFlowOcr.App"
EOF
chmod +x "${OUTPUT_DIR}/setup-permissions.sh"

# 检查主程序产物
MAIN_BIN="${OUTPUT_DIR}/ScanFlowOcr.App"
if [[ -f "${MAIN_BIN}" ]]; then
    chmod +x "${MAIN_BIN}"
    BIN_SIZE=$(du -h "${MAIN_BIN}" | cut -f1)
    echo ""
    echo "========================================================"
    echo "ScanFlow-OCR Linux x64 发布成功！"
    echo "交付目录 : ${OUTPUT_DIR}"
    echo "主程序   : ScanFlowOcr.App (${BIN_SIZE})"
    echo "启动脚本 : run.sh（内置自动赋权与 LD_LIBRARY_PATH 配置）"
    echo "赋权脚本 : setup-permissions.sh"
    echo "启动方法 : 在 Linux 终端执行 bash ./run.sh"
    echo "========================================================"
    echo ""
else
    echo "错误: 未找到主程序文件 ${MAIN_BIN}"
    exit 1
fi
