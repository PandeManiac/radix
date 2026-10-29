#!/usr/bin/env bash
# Prepares the native libraries and fonts Radix.Editor.Tests needs for offscreen
# Skia rendering on Linux, without sudo. Packages are extracted into .local-native/
# (gitignored) and the script prints the environment to use.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="$root/.local-native"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

packages=(
  libfontconfig1
  libfreetype6
  libexpat1
  libpng16-16t64
  libbrotli1
  fonts-dejavu-core
)

echo "Downloading native test packages..."
(cd "$work" && apt-get download "${packages[@]}" >/dev/null)

mkdir -p "$prefix"
for deb in "$work"/*.deb; do
  dpkg-deb -x "$deb" "$prefix"
done

cat > "$prefix/fonts.conf" <<EOF
<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd">
<fontconfig>
  <dir>$prefix/usr/share/fonts</dir>
  <cachedir>/tmp/radix-fontcache</cachedir>
</fontconfig>
EOF

cat <<EOF
Native test dependencies prepared in $prefix

Run the editor tests with:
  export LD_LIBRARY_PATH="$prefix/usr/lib/x86_64-linux-gnu"
  export FONTCONFIG_FILE="$prefix/fonts.conf"
  dotnet test tests/Radix.Editor.Tests/Radix.Editor.Tests.csproj
EOF
