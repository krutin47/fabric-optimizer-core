#!/bin/sh
# Starts the DreamGlass playground and opens it in the browser. Press Ctrl+C to stop.
cd "$(dirname "$0")" || exit 1
exec ./DreamGlass.Playground "$@"
