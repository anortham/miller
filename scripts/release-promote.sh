#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat >&2 <<'EOF'
Usage:
  scripts/release-promote.sh --version <version> (--run-id <id> | --artifacts-dir <dir>) [options]

Options:
  --repo <owner/name>          GitHub repository. Defaults to GITHUB_REPOSITORY or anortham/miller.
  --target <sha>              Target commit for a new tag. Defaults to the source run head SHA.
  --prerelease <true|false>   Defaults to true when the version contains '-'.
  --allow-overwrite           Allow updating an existing stable, non-draft release.
  --allow-overwrite <bool>    Boolean form for GitHub Actions inputs.
  --notes-file <path>         Defaults to docs/release-notes/v<version>.md when present.
  --dry-run                   Verify artifacts and print what would be released, then stop.

Promotes already validated release artifacts. Use after a package-only release
workflow run succeeds, so publishing does not rebuild the full platform matrix.
EOF
}

version=""
run_id=""
artifacts_dir=""
repo="${GITHUB_REPOSITORY:-anortham/miller}"
target_sha=""
prerelease=""
allow_overwrite="${ALLOW_OVERWRITE:-false}"
notes_file=""
dry_run="false"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      version="${2:-}"
      shift 2
      ;;
    --run-id)
      run_id="${2:-}"
      shift 2
      ;;
    --artifacts-dir)
      artifacts_dir="${2:-}"
      shift 2
      ;;
    --repo)
      repo="${2:-}"
      shift 2
      ;;
    --target)
      target_sha="${2:-}"
      shift 2
      ;;
    --prerelease)
      prerelease="${2:-}"
      shift 2
      ;;
    --allow-overwrite)
      if [[ $# -gt 1 && -z "${2:-}" ]]; then
        allow_overwrite="false"
        shift 2
      elif [[ "${2:-}" == "true" || "${2:-}" == "false" ]]; then
        allow_overwrite="$2"
        shift 2
      else
        allow_overwrite="true"
        shift
      fi
      ;;
    --notes-file)
      notes_file="${2:-}"
      shift 2
      ;;
    --dry-run)
      dry_run="true"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 2
      ;;
  esac
done

if [[ -z "$version" ]]; then
  echo "--version is required" >&2
  usage
  exit 2
fi

if [[ -n "$run_id" && -n "$artifacts_dir" ]]; then
  echo "Pass either --run-id or --artifacts-dir, not both" >&2
  exit 2
fi

if [[ -z "$run_id" && -z "$artifacts_dir" ]]; then
  echo "One of --run-id or --artifacts-dir is required" >&2
  usage
  exit 2
fi

version="${version#v}"
tag="v${version}"

if [[ -z "$prerelease" ]]; then
  prerelease="false"
  if [[ "$version" == *-* ]]; then
    prerelease="true"
  fi
fi

if [[ "$prerelease" != "true" && "$prerelease" != "false" ]]; then
  echo "--prerelease must be true or false" >&2
  exit 2
fi

if [[ "$allow_overwrite" != "true" && "$allow_overwrite" != "false" ]]; then
  echo "--allow-overwrite must be true or false" >&2
  exit 2
fi

if [[ -z "$notes_file" ]]; then
  candidate="docs/release-notes/${tag}.md"
  if [[ -f "$candidate" ]]; then
    notes_file="$candidate"
  fi
fi

tmp_root="$(mktemp -d)"
trap 'rm -rf "$tmp_root"' EXIT

download_root="$tmp_root/download"
release_dir="$tmp_root/release"
mkdir -p "$download_root" "$release_dir"

if [[ -n "$run_id" ]]; then
  status="$(gh run view "$run_id" --repo "$repo" --json status --jq .status)"
  conclusion="$(gh run view "$run_id" --repo "$repo" --json conclusion --jq .conclusion)"
  head_sha="$(gh run view "$run_id" --repo "$repo" --json headSha --jq .headSha)"
  if [[ "$status" != "completed" || "$conclusion" != "success" ]]; then
    echo "Source run $run_id is not a successful completed run: status=$status conclusion=$conclusion" >&2
    exit 1
  fi
  if [[ -z "$target_sha" ]]; then
    target_sha="$head_sha"
  fi
  gh run download "$run_id" --repo "$repo" -D "$download_root"
else
  cp -R "$artifacts_dir"/. "$download_root"/
fi

if [[ -z "$target_sha" ]]; then
  target_sha="${GITHUB_SHA:-}"
fi

if [[ -z "$target_sha" ]]; then
  echo "A target commit is required when promoting from --artifacts-dir outside GitHub Actions" >&2
  exit 2
fi

while IFS= read -r -d '' file; do
  name="$(basename "$file")"
  case "$name" in
    miller-"$version"-*)
      cp "$file" "$release_dir/$name"
      ;;
  esac
done < <(find "$download_root" -type f -print0)

expected_targets=(
  "aarch64-apple-darwin:.tar.gz"
  "x86_64-apple-darwin:.tar.gz"
  "x86_64-unknown-linux-gnu:.tar.gz"
  "x86_64-pc-windows-msvc:.zip"
)

for item in "${expected_targets[@]}"; do
  release_target="${item%%:*}"
  extension="${item#*:}"
  archive_name="miller-${version}-${release_target}${extension}"
  archive="$release_dir/$archive_name"
  sidecar="$archive.sha256"

  if [[ ! -f "$archive" ]]; then
    echo "Missing release archive: $archive_name" >&2
    exit 1
  fi
  if [[ ! -f "$sidecar" ]]; then
    echo "Missing checksum sidecar: ${archive_name}.sha256" >&2
    exit 1
  fi

  sidecar_line="$(tr -d '\r' < "$sidecar" | head -n 1)"
  expected_hash="$(printf '%s\n' "$sidecar_line" | awk '{print $1}')"
  expected_name="$(printf '%s\n' "$sidecar_line" | awk '{print $2}')"
  actual_hash="$(shasum -a 256 "$archive" | awk '{print $1}')"

  if [[ ! "$expected_hash" =~ ^[a-f0-9]{64}$ ]]; then
    echo "Invalid checksum in ${archive_name}.sha256" >&2
    exit 1
  fi
  if [[ -n "$expected_name" && "$expected_name" != "$archive_name" ]]; then
    echo "Checksum sidecar ${archive_name}.sha256 names $expected_name, expected $archive_name" >&2
    exit 1
  fi
  if [[ "$expected_hash" != "$actual_hash" ]]; then
    echo "Checksum mismatch for $archive_name" >&2
    exit 1
  fi
done

echo "Promoting $tag from artifacts:"
ls -lh "$release_dir"

if [[ "$dry_run" == "true" ]]; then
  echo "Dry run complete; release was not modified."
  exit 0
fi

release_exists="false"
if gh release view "$tag" --repo "$repo" >/dev/null 2>&1; then
  release_exists="true"
fi

if [[ "$release_exists" == "true" ]]; then
  is_prerelease="$(gh api "repos/${repo}/releases/tags/${tag}" --jq .prerelease)"
  is_draft="$(gh api "repos/${repo}/releases/tags/${tag}" --jq .draft)"
  if [[ "$is_prerelease" == "false" && "$is_draft" == "false" && "$allow_overwrite" != "true" ]]; then
    echo "Refusing to overwrite published release $tag. Re-run with --allow-overwrite to force." >&2
    exit 1
  fi

  release_id="$(gh api "repos/${repo}/releases/tags/${tag}" --jq .id)"
  if [[ -n "$notes_file" && -f "$notes_file" ]]; then
    release_body="$(cat "$notes_file")"
  else
    release_body="Miller $tag"
  fi
  gh api --method PATCH "repos/${repo}/releases/${release_id}" \
    -f name="Miller ${tag}" \
    -f body="$release_body" \
    -F prerelease="$prerelease" >/dev/null
  gh release upload "$tag" "$release_dir"/* --repo "$repo" --clobber
else
  args=(
    gh release create "$tag" "$release_dir"/*
    --repo "$repo"
    --target "$target_sha"
    --title "Miller $tag"
  )
  if [[ -n "$notes_file" && -f "$notes_file" ]]; then
    args+=(--notes-file "$notes_file")
  else
    args+=(--notes "Miller $tag")
  fi
  if [[ "$prerelease" == "true" ]]; then
    args+=(--prerelease)
  else
    args+=(--latest)
  fi
  "${args[@]}"
fi
