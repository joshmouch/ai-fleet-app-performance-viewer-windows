export LC_ALL=C
set -f

start_time() {
    IFS= read -r stat < "$1/stat" || return 1
    set -- ${stat##*) }
    [ "$#" -ge 20 ] || return 1
    printf '%s' "${20}"
}

for pid do
    proc=/proc/$pid
    started=$(start_time "$proc" 2>/dev/null) || continue
    IFS= read -r name < "$proc/comm" 2>/dev/null || continue
    name=$(printf '%s' "$name" | tr '\011\012\015' '   ')
    args=$(head -c 8192 "$proc/cmdline" 2>/dev/null | tr '\000\011\012\015' '    ')
    [ -n "$args" ] && [ "$started" = "$(start_time "$proc" 2>/dev/null)" ] || continue
    printf '%s\t%s\t%s\t%s\n' "$pid" "$started" "$name" "${args% }"
done
