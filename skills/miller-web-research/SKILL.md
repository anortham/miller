---
name: miller-web-research
description: "Use when researching web pages with Miller: fetch token-optimized markdown via browser39, import it as web content, then search/read bounded windows through Miller's content corpus."
---

# Miller Web Research

Use this workflow when web content should become searchable/readable through Miller without writing files under the repo.

## Prerequisite

Check for browser39 first:

```bash
command -v browser39
```

If it is missing, tell the user:

```text
browser39 is required for Miller web research. Install it with: cargo install browser39
```

## Fetch And Import

Fetch into a temp markdown file, then import that file into Miller as `web` content:

```bash
tmp="$(mktemp -t miller-web.XXXXXX.md)"
cmd="$(mktemp -t miller-web.XXXXXX.jsonl)"
out="$(mktemp -t miller-web.XXXXXX.out.jsonl)"
url="https://example.com/page"
printf '{"id":"1","action":"fetch","v":1,"seq":1,"url":"%s","options":{"selector":"article","max_tokens":12000,"strip_nav":true,"include_links":true,"include_images":false}}\n' "$url" > "$cmd"
browser39 batch "$cmd" --output "$out"
title="$(python3 - "$out" "$tmp" <<'PY'
import json, sys
source, target = sys.argv[1], sys.argv[2]
with open(source, 'r', encoding='utf-8') as f:
    row = json.loads(f.readline())
markdown = row.get('markdown') or ''
title = row.get('title') or row.get('url') or 'web page'
with open(target, 'w', encoding='utf-8') as f:
    f.write(markdown)
print(title)
PY
)"
miller content add-markdown "$tmp" --url "$url" --display-path "$title" --json
```

## Search And Read

Search only imported web content:

```bash
miller content search "important phrase" --kind web
```

Read a bounded line window from a hit:

```bash
miller content read --source-id SOURCE_ID --line 120 --context-lines 10
```

## Rules

- Do not create or modify repo files for web imports.
- Do not write pages under `docs` or any tracked directory.
- Do not paste full page markdown into the conversation.
- Use `content search --kind web` first, then `content read` with a small window.
- If browser39 returns truncated content, fetch the next offset and append it to the same temp markdown before one import.
