#!/usr/bin/env bash
# Esporta il business plan in HTML autonomo (un file per documento) con pandoc.
# Uso: scripts/business-plan/export.sh            → docs/business-plan/out/*.html
# Il PDF si ottiene aprendo l'HTML nel browser e stampando (la cartella out/ è ignorata da git).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DIR="$ROOT/docs/business-plan"
OUT="$DIR/out"
mkdir -p "$OUT"

command -v pandoc >/dev/null 2>&1 || { echo "pandoc non trovato: installalo (https://pandoc.org/installing.html)" >&2; exit 1; }

for name in BUSINESS-PLAN LISTINO MODELLO-FINANZIARIO VERIFICA-PIANI-B2C PITCH; do
  src="$DIR/$name.md"
  dst="$OUT/$name.html"
  pandoc "$src" \
    --from gfm \
    --standalone --embed-resources \
    --toc --toc-depth=2 \
    --metadata lang=it \
    --metadata pagetitle="CasaZen - $name" \
    --css "$DIR/style.css" \
    --output "$dst"
  echo "scritto $dst"
done
