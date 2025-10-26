#!/bin/bash
set -e

# ------------------------------
# Publish .NET Web App for Windows
# ------------------------------

# Configuration
PROJECT_FILE="src/Bergdahl.NodePad.WebApp/Bergdahl.NodePad.WebApp.csproj"     # Change to your project file
OUTPUT_DIR="./publish"     # Output folder
RUNTIME="win-x64"                  # Windows 64-bit; use win-arm64 for ARM devices (e.g. Surface)
CONFIGURATION="Release"

echo "🚀 Publishing $PROJECT_FILE for Windows ($RUNTIME)..."

# Clean previous build
dotnet clean "$PROJECT_FILE" -c "$CONFIGURATION"

# Publish
dotnet publish "$PROJECT_FILE" \
    -c "$CONFIGURATION" \
    -r "$RUNTIME" \
    --self-contained false \
    -o "$OUTPUT_DIR"

echo ""
echo "✅ Publish complete!"
echo "📂 Output folder: $OUTPUT_DIR"
echo "▶️ To run on Windows:"
echo "   $(basename "${PROJECT_FILE%.*}").exe"
