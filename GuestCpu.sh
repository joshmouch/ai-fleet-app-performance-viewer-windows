set -eu
export LC_ALL=C

snapshot() {
    printf '%s\n' snapshot
    IFS= read -r uptime < /proc/uptime
    printf 'time %s\n' "$uptime"
    while IFS= read -r cpu; do
        case "$cpu" in
            cpu*) printf '%s\n' "$cpu" ;;
            *) break ;;
        esac
    done < /proc/stat
    for stat in /proc/[0-9]*/stat; do
        if IFS= read -r record 2>/dev/null < "$stat"; then
            printf '%s\n' "$record"
        fi
    done
}

ticks=$(getconf CLK_TCK)
{ snapshot; sleep 1; snapshot; } | awk -v ticks="$ticks" '
    /^snapshot$/ { sample++; next }
    /^time / { time[sample] = $2; next }
    /^cpu / {
        # guest and guest_nice are already included in user and nice.
        busy[sample] = $2 + $3 + $4 + $7 + $8
        next
    }
    /^cpu[0-9]+ / { cpus[sample]++; next }
    /^[0-9]+ \(/ {
        fields = $0
        sub(/^.*\) /, "", fields)
        if (split(fields, stat, " ") < 20) next
        pid = $1
        identity = pid SUBSEP stat[20]
        used = stat[12] + stat[13]
        if (sample == 1) previous[identity] = used
        else if (identity in previous && used > previous[identity]) {
            delta = used - previous[identity]
            process_ticks += delta
            name = substr($0, length(pid) + 3, length($0) - length(fields) - length(pid) - 4)
            gsub(/[[:cntrl:]]/, " ", name)
            printf "%d\t%d\t%s\t%s\n", pid, delta, stat[20], name
        }
    }
    END {
        if (sample != 2 || ticks <= 0 || time[2] <= time[1] || cpus[2] <= 0) exit 1
        printf "sample\t%.6f\t%d\t%d\t%d\t%d\n", time[2] - time[1], ticks, busy[2] - busy[1], process_ticks, cpus[2]
    }
'
