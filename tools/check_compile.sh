#!/usr/bin/env bash
# Compile-check the Unity scripts with Unity 5.6's own compiler, without opening the editor
# (so it works while the editor has the project open). Mirrors how Unity builds the editor
# assembly: our scripts + MeatKit + OtherLoader, with the imported game assemblies.
#
# Usage: tools/check_compile.sh [path-to-Unity-5.6.7f1]
set -euo pipefail

UNITY="${1:-X:/Tools/Unity/5.6.7f1}/Editor/Data"
ASSETS="$(cd "$(dirname "$0")/../Unity/Assets" && pwd)"
OUT="${TEMP:-/tmp}/originsstaffs_compile_check.dll"

cd "$ASSETS"
# IconCamera's runtime half lives in a folder this check doesn't model; Unity compiles it fine.
mapfile -t SRC < <(find OriginsStaffs MeatKit Packages/Otherloader -name "*.cs" | grep -v IconCamera)
REFS=()
for dll in Managed/*.dll; do REFS+=("-r:$dll"); done

"$UNITY/MonoBleedingEdge/bin/mono.exe" "$UNITY/MonoBleedingEdge/lib/mono/4.5/mcs.exe" \
    -sdk:2 -target:library -define:H3VR_IMPORTED \
    -nowarn:0649,0168,0618,0219,0108,0414,1635,0169 \
    -out:"$OUT" \
    -r:"$UNITY/Managed/UnityEngine.dll" -r:"$UNITY/Managed/UnityEditor.dll" \
    -r:"$UNITY/UnityExtensions/Unity/GUISystem/UnityEngine.UI.dll" -r:System.Core.dll \
    "${REFS[@]}" "${SRC[@]}"
