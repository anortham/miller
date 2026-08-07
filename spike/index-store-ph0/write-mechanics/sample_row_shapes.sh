#!/usr/bin/env bash
# Read-only sampling of the live Miller artifact. Produces the row-shape numbers
# that lib/shapes.py is sized from. Never opens the artifact for write.
set -euo pipefail

ARTIFACT="${MILLER_ARTIFACT:-/Users/murphy/source/miller/.miller/symbols.db}"
OUT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/out/row-shapes.txt}"

if [[ ! -f "$ARTIFACT" ]]; then
  echo "artifact not found: $ARTIFACT" >&2
  exit 1
fi

URI="file:${ARTIFACT}?mode=ro"
SQLITE=(sqlite3 -cmd ".timeout 60000")

{
  echo "# Row shapes sampled from ${ARTIFACT}"
  echo "# sqlite3 CLI: $(sqlite3 --version)"
  echo
  echo "## pragmas"
  "${SQLITE[@]}" "$URI" "SELECT 'page_size='||(SELECT * FROM pragma_page_size()),
                         'page_count='||(SELECT * FROM pragma_page_count()),
                         'auto_vacuum='||(SELECT * FROM pragma_auto_vacuum()),
                         'freelist_count='||(SELECT * FROM pragma_freelist_count());"
  echo
  echo "## row counts"
  "${SQLITE[@]}" -header -column "$URI" "
    SELECT 'files' AS tbl, COUNT(*) AS rows FROM files
    UNION ALL SELECT 'symbols', COUNT(*) FROM symbols
    UNION ALL SELECT 'reference_sites', COUNT(*) FROM reference_sites
    UNION ALL SELECT 'identifiers', COUNT(*) FROM identifiers;"
  echo
  echo "## physical bytes per btree (dbstat)"
  "${SQLITE[@]}" -header -column "$URI" "
    SELECT name, SUM(pgsize) AS bytes, SUM(ncell) AS cells
    FROM dbstat
    WHERE name IN ('identifiers','reference_sites','symbols')
       OR name LIKE 'idx_identifiers%' OR name LIKE 'idx_reference_sites%'
       OR name LIKE 'idx_symbols%' OR name LIKE 'sqlite_autoindex_identifiers%'
       OR name LIKE 'sqlite_autoindex_reference_sites%'
       OR name LIKE 'sqlite_autoindex_symbols%'
    GROUP BY name ORDER BY bytes DESC;"
  echo
  echo "## average text column lengths (NULL counted as 0)"
  "${SQLITE[@]}" -header -column "$URI" "
    SELECT 'identifiers' AS tbl,
      ROUND(AVG(LENGTH(identifier_id)),1) AS c_id,
      ROUND(AVG(LENGTH(reference_site_id)),1) AS c_rsid,
      ROUND(AVG(LENGTH(file_id)),1) AS c_fileid,
      ROUND(AVG(LENGTH(path)),1) AS c_path,
      ROUND(AVG(LENGTH(name)),1) AS c_name,
      ROUND(AVG(LENGTH(kind)),1) AS c_kind,
      ROUND(AVG(LENGTH(COALESCE(containing_symbol_id,''))),1) AS c_containing,
      ROUND(AVG(LENGTH(COALESCE(target_symbol_id,''))),1) AS c_target,
      ROUND(AVG(LENGTH(COALESCE(metadata_json,''))),1) AS c_meta
    FROM identifiers;"
  "${SQLITE[@]}" -header -column "$URI" "
    SELECT 'reference_sites' AS tbl,
      ROUND(AVG(LENGTH(reference_site_id)),1) AS c_rsid,
      ROUND(AVG(LENGTH(file_id)),1) AS c_fileid,
      ROUND(AVG(LENGTH(path)),1) AS c_path,
      ROUND(AVG(LENGTH(COALESCE(containing_symbol_id,''))),1) AS c_containing,
      ROUND(AVG(LENGTH(provenance)),1) AS c_prov,
      ROUND(AVG(is_exact),3) AS exact_fraction
    FROM reference_sites;"
  "${SQLITE[@]}" -header -column "$URI" "
    SELECT 'symbols' AS tbl,
      ROUND(AVG(LENGTH(symbol_id)),1) AS c_id,
      ROUND(AVG(LENGTH(path)),1) AS c_path,
      ROUND(AVG(LENGTH(name)),1) AS c_name,
      ROUND(AVG(LENGTH(kind)),1) AS c_kind,
      ROUND(AVG(LENGTH(COALESCE(signature,''))),1) AS c_sig,
      ROUND(AVG(LENGTH(COALESCE(doc_comment,''))),1) AS c_doc,
      ROUND(AVG(LENGTH(COALESCE(visibility,''))),1) AS c_vis,
      ROUND(AVG(LENGTH(COALESCE(body_hash,''))),1) AS c_bodyhash,
      ROUND(AVG(LENGTH(COALESCE(metadata_json,''))),1) AS c_meta
    FROM symbols;"
  echo
  echo "## rows per file"
  "${SQLITE[@]}" -header -column "$URI" "
    SELECT ROUND((SELECT COUNT(*) FROM identifiers)*1.0/(SELECT COUNT(*) FROM files),2) AS identifiers_per_file,
           ROUND((SELECT COUNT(*) FROM reference_sites)*1.0/(SELECT COUNT(*) FROM files),2) AS ref_sites_per_file,
           ROUND((SELECT COUNT(*) FROM symbols)*1.0/(SELECT COUNT(*) FROM files),2) AS symbols_per_file;"
} > "$OUT"

echo "wrote $OUT"
