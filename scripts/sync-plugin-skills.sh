#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rm -rf "${repo_root}/skills"
mkdir -p "${repo_root}/skills"
cp -R "${repo_root}/.agents/skills/." "${repo_root}/skills/"
