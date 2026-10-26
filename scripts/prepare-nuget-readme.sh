#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
input_readme="${1:-$repo_root/README.md}"
output_readme="${2:-$repo_root/artifacts/nuget/README.md}"

if [ ! -f "$input_readme" ]; then
  echo "Input README not found: $input_readme" >&2
  exit 1
fi

mkdir -p "$(dirname "$output_readme")"

cat <<'EOF' > "$output_readme"
# Acplite

**Your coding agents. One protocol away.**

Native C# / NativeAOT Agent Client Protocol (ACP) SDK and CLI bridges for **Claude Code**, **OpenAI Codex**, **Grok**, and **Google Antigravity**.

[![Latest release](https://img.shields.io/github/v/release/cristipufu/acplite?style=flat-square&label=release&labelColor=0f172a&color=0ea5e9)](https://github.com/cristipufu/acplite/releases/latest)
[![Acplite NuGet](https://img.shields.io/nuget/v/Acplite?style=flat-square&logo=nuget&logoColor=white&label=Acplite&labelColor=0f172a&color=0284c7)](https://www.nuget.org/packages/Acplite/)
[![Acplite.Sdk NuGet](https://img.shields.io/nuget/v/Acplite.Sdk?style=flat-square&logo=nuget&logoColor=white&label=Acplite.Sdk&labelColor=0f172a&color=0284c7)](https://www.nuget.org/packages/Acplite.Sdk/)
[![Build status](https://img.shields.io/github/actions/workflow/status/cristipufu/acplite/ci.yml?branch=main&style=flat-square&label=build&logo=github&logoColor=white&labelColor=0f172a)](https://github.com/cristipufu/acplite/actions/workflows/ci.yml)
[![MIT license](https://img.shields.io/badge/license-MIT-64748b?style=flat-square&labelColor=0f172a)](https://github.com/cristipufu/acplite/blob/main/LICENSE)

EOF

awk 'found { print } /^---$/ && !found { found=1; print }' "$input_readme" >> "$output_readme"
