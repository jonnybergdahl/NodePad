#!/bin/bash

set -e

# ------------------------------
# Publish .NET Web App for macOS
# ------------------------------

# Configuration
PROJECT_FILE="src/Bergdahl.NodePad.WebApp/Bergdahl.NodePad.WebApp.csproj"     # Change this to your project file
OUTPUT_DIR="./publish"             # Output folder
RUNTIME="osx-arm64"                # For Apple Silicon; use osx-x64 for Intel Macs
CONFIGURATION="Release"

echo "🚀 Publishing $PROJECT_FILE for macOS ($RUNTIME)..."

# Clean previous build
dotnet clean "$PROJECT_FILE" -c "$CONFIGURATION"

# Publish
dotnet publish "$PROJECT_FILE" \
    -c "$CONFIGURATION" \
    -r "$RUNTIME" \
    --self-contained true \
    -o "$OUTPUT_DIR"

echo ""
echo "✅ Publish complete!"
echo "📂 Output folder: $OUTPUT_DIR"
echo "▶️ To run the app:"
echo "   $OUTPUT_DIR/$(basename "${PROJECT_FILE%.*}")"
