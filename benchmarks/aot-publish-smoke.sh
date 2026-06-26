#!/usr/bin/env bash
# aot-publish-smoke.sh
#
# CI gate: publish McpIt.AotCheck with full native AOT, then scan the build
# output for any IL2026/IL3050/IL30xx warning that originates from McpIt
# source files or the McpIt assembly. Exits 1 if any such warning is found.
#
# Requirements: .NET 10 SDK + native AOT toolchain (clang/LLVM on Linux,
# Xcode Command Line Tools on macOS, Visual C++ build tools on Windows).
# This script is intentionally not runnable on machines that lack the native
# toolchain; it documents the CI gate for environments that provide it
# (e.g. GitHub Actions ubuntu-latest with build-essential installed).
#
# Usage (from the repository root):
#   bash benchmarks/aot-publish-smoke.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
AOT_CHECK_PROJ="${REPO_ROOT}/benchmarks/McpIt.AotCheck/McpIt.AotCheck.csproj"
OUT_DIR="${REPO_ROOT}/artifacts/aot-smoke"

echo "=== AOT publish smoke: McpIt.AotCheck ==="
echo "Project:   ${AOT_CHECK_PROJ}"
echo "Output:    ${OUT_DIR}"
echo ""

# Keep a copy of stdout+stderr for post-publish grep.
BUILD_LOG="$(mktemp)"
trap 'rm -f "${BUILD_LOG}"' EXIT

dotnet publish "${AOT_CHECK_PROJ}" \
    -f net10.0 \
    -c Release \
    -p:PublishAot=true \
    --output "${OUT_DIR}" \
    2>&1 | tee "${BUILD_LOG}"

echo ""
echo "=== Scanning for McpIt-origin AOT/trim warnings ==="

# Match lines that contain an IL2026, IL3050, or any other IL30xx diagnostic
# AND reference McpIt source paths or the McpIt assembly name. This deliberately
# excludes warnings from third-party dependencies (ModelContextProtocol.AspNetCore,
# Microsoft.AspNetCore.*) that are outside McpIt's own AOT gate.
#
# Patterns matched:
#   src/McpIt/...cs(n,m): warning IL2026:
#   warning IL2026 [McpIt]
#   warning IL3050 ... McpIt ...
MCPIT_WARNINGS=$(grep -E "(IL2026|IL3050|IL30[0-9]{2})" "${BUILD_LOG}" \
    | grep -iE "(McpIt|src/McpIt)" \
    || true)

if [[ -n "${MCPIT_WARNINGS}" ]]; then
    echo ""
    echo "FAIL: McpIt-origin AOT/trim warnings found:"
    echo "${MCPIT_WARNINGS}"
    exit 1
fi

echo "PASS: no McpIt-origin IL2026/IL3050/IL30xx warnings in AOT publish output."
