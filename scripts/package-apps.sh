#!/usr/bin/env bash
# Builds the download-and-run playground packages: one zip per operating system, each with a self-contained program
# (no .NET needed on the target computer), the page files and a short README.txt.
#
#   scripts/package-apps.sh [output-dir] [rid ...]
#
# Defaults: output to ./dist, for win-x64 osx-arm64 osx-x64 linux-x64. Needs the .NET 10 SDK and `zip`; the first run
# downloads the runtime packs for each target from NuGet.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$(mkdir -p "${1:-$repo/dist}" && cd "${1:-$repo/dist}" && pwd)"
shift || true
rids=("$@")
[ ${#rids[@]} -eq 0 ] && rids=(win-x64 osx-arm64 osx-x64 linux-x64)

project="$repo/src/DreamGlass.Optimizer.Playground/DreamGlass.Optimizer.Playground.csproj"
templates="$repo/scripts/package-files"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

for rid in "${rids[@]}"; do
  case "$rid" in
    win-*)   os=windows ;;
    osx-*)   os=macos ;;
    linux-*) os=linux ;;
    *) echo "Unsupported runtime: $rid" >&2; exit 1 ;;
  esac
  name="DreamGlass-Playground-$rid"
  dir="$work/$name/DreamGlass-Playground"
  echo "== $rid"
  dotnet publish "$project" -c Release -r "$rid" -o "$dir" -nologo -v quiet

  # Only the program, its page files and the instructions: drop the IIS config and build manifests.
  rm -f "$dir/web.config" "$dir"/*.json "$dir"/*.pdb "$dir"/*.xml
  find "$dir/wwwroot" -name '*.gz' -o -name '*.br' | xargs -r rm -f
  if [ "$os" = windows ]; then sed 's/$/\r/' "$templates/README-$os.txt" > "$dir/README.txt"  # Notepad-friendly
  else cp "$templates/README-$os.txt" "$dir/README.txt"; fi
  case "$os" in
    macos) cp "$templates/Start DreamGlass.command" "$dir/" ;;
    linux) cp "$templates/start.sh" "$dir/" ;;
  esac

  # Permissions travel inside the zip, so the program and start script stay runnable after unzipping on macOS/Linux.
  find "$dir" -type d -exec chmod 755 {} +
  find "$dir" -type f -exec chmod 644 {} +
  [ "$os" = windows ] || chmod 755 "$dir/DreamGlass.Playground" "$dir"/*.command "$dir"/*.sh 2>/dev/null || true

  rm -f "$out/$name.zip"
  (cd "$work/$name" && zip -qr -X "$out/$name.zip" DreamGlass-Playground)
  echo "   $out/$name.zip ($(du -h "$out/$name.zip" | cut -f1))"
  rm -rf "$work/$name"
done
