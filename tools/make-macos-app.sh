#!/bin/bash
# Monta "Network Backup Tool.app" e o zip para a release. Roda num Mac (GitHub Actions macos).
# Uso: tools/make-macos-app.sh <osx-arm64|osx-x64> <versão> <arquivo.zip>
set -euo pipefail
RID="$1"
VERSION="$2"
OUT="$3"

dotnet publish src/NetBackupTool.App -c Release -r "$RID" -o "build/$RID" -p:Version="$VERSION"

APP="build/$RID-app/Network Backup Tool.app"
rm -rf "build/$RID-app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "build/$RID/NetBackupTool" "$APP/Contents/MacOS/"
cp assets/icon.icns "$APP/Contents/Resources/icon.icns"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Network Backup Tool</string>
  <key>CFBundleDisplayName</key><string>Network Backup Tool</string>
  <key>CFBundleIdentifier</key><string>io.github.leddzeppellin.netbackuptool</string>
  <key>CFBundleExecutable</key><string>NetBackupTool</string>
  <key>CFBundleIconFile</key><string>icon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <!-- macOS 15+: sem esta frase o sistema bloqueia o acesso à rede local -->
  <key>NSLocalNetworkUsageDescription</key>
  <string>Network Backup Tool copies files between computers on your local network.</string>
</dict>
</plist>
EOF

# Assinatura ad hoc: sem ela o macOS em Apple Silicon nem executa o binário.
codesign --force --deep --sign - "$APP"
ditto -c -k --keepParent "$APP" "$OUT"
echo "Gerado: $OUT"
