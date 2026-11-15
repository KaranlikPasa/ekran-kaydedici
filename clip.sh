#!/usr/bin/env bash
set -euo pipefail

DEST="tr.eymen.EkranKaydedici"
OBJ="/tr/eymen/EkranKaydedici"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if gdbus call --session --dest "$DEST" --object-path "$OBJ" \
    --method org.gtk.Actions.Activate "save-clip" "[]" "{}" >/dev/null 2>&1; then
    exit 0
fi

# App isn't running yet: start it in background so subsequent F8 presses work immediately.
setsid -f "$SCRIPT_DIR/launch.sh" --background >/dev/null 2>&1 < /dev/null
notify-send -a "Ekran Kaydedici" -i ekran-kaydedici \
    "Ekran Kaydedici başlatıldı" \
    "Arka plan kaydı hazırlanıyor, birkaç saniye sonra F8 ile klip alabilirsiniz."
