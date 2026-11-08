#!/bin/zsh
# Builds and registers "3MF Library Opener.app": the macOS handler for threemflib:// links, so the library site can
# open the local (synced) copy of a model in Bambu Studio instead of downloading it.
#
#   tools/mac-opener/build.sh                      # library in OneDrive's "3D Print", app in ~/Applications
#   tools/mac-opener/build.sh "/path/to/library"   # another local library folder
#
# Uninstall: delete ~/Applications/3MF Library Opener.app.
set -euo pipefail

here="${0:A:h}"
library="${1:-$HOME/Library/CloudStorage/OneDrive-Personal/3D Print}"
app="$HOME/Applications/3MF Library Opener.app"

[[ -d "$library" ]] || { print -u2 "Library folder not found: $library"; exit 1; }
[[ "$library" != *'"'* && "$library" != *'$'* && "$library" != *'`'* ]] || { print -u2 "Unsupported characters in: $library"; exit 1; }

mkdir -p "${app:h}"
rm -rf "$app"
osacompile -o "$app" "$here/opener.applescript"

# The worker script, with the library folder filled in.
sed "s|__LIBRARY_ROOT__|${library//|/\\|}|" "$here/open-model.sh" > "$app/Contents/Resources/open-model.sh"
chmod 755 "$app/Contents/Resources/open-model.sh"
print -n -- "$library" > "$app/Contents/Resources/library-folder.txt"

# Claim the threemflib:// scheme; no Dock icon while it briefly runs.
plist="$app/Contents/Info.plist"
plutil -replace CFBundleIdentifier -string "net.ladenius.3mf-library-opener" "$plist"
plutil -replace CFBundleName -string "3MF Library Opener" "$plist"
plutil -replace LSUIElement -bool true "$plist"
plutil -replace CFBundleURLTypes -json '[{"CFBundleURLName":"3MF Library link","CFBundleURLSchemes":["threemflib"]}]' "$plist"

# Editing the bundle broke osacompile's signature; sign it again (ad hoc, local use only).
codesign --force --deep --sign - "$app" 2>/dev/null

/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$app"
print "Installed: $app"
print "Library:   $library"
