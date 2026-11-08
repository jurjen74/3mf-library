#!/bin/zsh
# Opens a model from the local copy of the library in a slicer.
#
#   open-model.sh 'threemflib://open?path=Stuff%2FLamp.3mf'               # Bambu Studio
#   open-model.sh 'threemflib://open?path=Stuff%2FLamp.3mf&slicer=orca'   # bambu | orca | prusa | cura
#
# Called by "3MF Library Opener.app" for threemflib:// links from the library site. Any web page can fire such a
# link, so this only opens model files that really sit inside the library folder, in one of the known slicers.
#
# Environment (for testing): LIBRARY_ROOT overrides the folder, DRY_RUN=1 prints "<slicer> <file>" instead of opening.
set -euo pipefail

root="${LIBRARY_ROOT:-__LIBRARY_ROOT__}"

fail() { print -u2 -- "$1"; exit "${2:-1}"; }
decode() { printf '%b' "${1//\%/\\x}"; }   # percent-decoding; UTF-8 bytes are fine (%E6%B0%B4 is 水)

url="${1:-}"
[[ "$url" =~ '^threemflib://open/?\?(.*)$' ]] || fail "Not a library link: $url" 2
query="${match[1]}"

relative="" slicer="bambu"
for pair in ${(s:&:)query}; do
  case "$pair" in
    path=*) relative="$(decode "${pair#path=}")" ;;
    slicer=*) slicer="${pair#slicer=}" ;;
  esac
done

case "$slicer" in
  bambu) open_with=(-b com.bambulab.bambu-studio); slicer_name="Bambu Studio" ;;
  orca)  open_with=(-a OrcaSlicer);                slicer_name="OrcaSlicer" ;;
  prusa) open_with=(-a PrusaSlicer);               slicer_name="PrusaSlicer" ;;
  cura)  open_with=(-a "UltiMaker Cura");          slicer_name="UltiMaker Cura" ;;
  *) fail "Unknown slicer: $slicer" 6 ;;
esac

# Library paths look like Folder/Sub/File.3mf: nothing absolute, no "." or ".." segments.
[[ -n "$relative" && "$relative" != /* && "/$relative/" != */./* && "/$relative/" != */../* && "$relative" != *//* ]] \
  || fail "Refused path: $relative" 3

case "${relative:l}" in
  *.3mf|*.stl|*.obj|*.step|*.stp) ;;
  *) fail "Only model files can be opened: $relative" 4 ;;
esac

file="$root/$relative"
[[ -f "$file" ]] || fail "Not in your local library: $relative" 5

# Belt and braces: the resolved file must still be inside the library (no symlinks pointing out).
real_root="${root:A}"
real_file="${file:A}"
[[ "$real_file" == "$real_root"/* ]] || fail "Refused path outside the library: $relative" 3

if [[ -n "${DRY_RUN:-}" ]]; then
  print -- "$slicer $real_file"
else
  open "${open_with[@]}" "$real_file" 2>/dev/null || fail "$slicer_name isn't installed on this Mac." 7
fi
