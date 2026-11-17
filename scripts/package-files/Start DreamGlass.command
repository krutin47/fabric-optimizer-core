#!/bin/sh
# Double-click in Finder to start the DreamGlass playground. Keep this Terminal window open while you use it.
cd "$(dirname "$0")" || exit 1
# Clear the "downloaded from the internet" flag on the program next to this script, in case the README step was skipped.
xattr -dr com.apple.quarantine . 2>/dev/null
chmod +x ./DreamGlass.Playground 2>/dev/null
exec ./DreamGlass.Playground "$@"
