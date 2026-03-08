#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
dotnet run --project "$SCRIPT_DIR/src/MusicScanner" -- "${1:-$SCRIPT_DIR/music}"
