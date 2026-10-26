#!/usr/bin/env bash
# Runs the standalone Linux or macOS build of mailnix (building it first if needed) and opens it in the browser.
#
# Starts <output>/mailnix in this terminal; press Ctrl+C to stop it. Data stays in <output>/data.
#
# Examples:
#   ./scripts/run-unix.sh
#   ./scripts/run-unix.sh --build --urls http://localhost:8080

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: run-unix.sh [options]

  --runtime <rid>   linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64, osx-x64 or osx-arm64
                    (default: this machine)
  --output <dir>    same default as build-unix.sh: artifacts/<linux|macos>/<rid> in the repository
  --urls <urls>     address(es) to listen on, separated by ';' (default: http://localhost:5000)
  --build           rebuild even if a build exists
  --no-browser      don't open the browser
  -h, --help        show this help
EOF
}

die() { printf 'error: %s\n' "$*" >&2; exit 1; }

detect_runtime() {
    local os arch libc=''
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$arch" in
        x86_64 | amd64) arch=x64 ;;
        aarch64 | arm64) arch=arm64 ;;
        *) die "unsupported CPU architecture '$arch'; pass --runtime" ;;
    esac
    case "$os" in
        Linux)
            if ldd --version 2>&1 | grep -qi musl; then libc='musl-'; fi
            echo "linux-$libc$arch"
            ;;
        Darwin) echo "osx-$arch" ;;
        *) die "unsupported OS '$os'; pass --runtime (on Windows use run-windows.ps1)" ;;
    esac
}

# PIDs of processes running the given executable.
running_pids() {
    local exe="$1" link
    if [ -d /proc/self ]; then
        for link in /proc/[0-9]*/exe; do
            if [ "$(readlink "$link" 2>/dev/null)" = "$exe" ]; then
                link="${link#/proc/}"
                echo "${link%/exe}"
            fi
        done
    elif command -v lsof >/dev/null 2>&1; then
        lsof -t -- "$exe" 2>/dev/null || true
    fi
}

runtime=''
output=''
urls='http://localhost:5000'
build=false
browser=true
while [ $# -gt 0 ]; do
    case "$1" in
        --runtime) [ $# -ge 2 ] || die "$1 needs a value"; runtime="$2"; shift 2 ;;
        --output) [ $# -ge 2 ] || die "$1 needs a value"; output="$2"; shift 2 ;;
        --urls) [ $# -ge 2 ] || die "$1 needs a value"; urls="$2"; shift 2 ;;
        --build) build=true; shift ;;
        --no-browser) browser=false; shift ;;
        -h | --help) usage; exit 0 ;;
        *) usage >&2; die "unknown option '$1'" ;;
    esac
done

[ -n "$runtime" ] || runtime="$(detect_runtime)"
case "$runtime" in
    linux-*) platform=linux ;;
    osx-*) platform=macos ;;
    *) die "unsupported runtime '$runtime'" ;;
esac

scripts="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -n "$output" ] || output="$(dirname "$scripts")/artifacts/$platform/$runtime"
mkdir -p "$output"
output="$(cd "$output" && pwd)"
exe="$output/mailnix"

if [ "$build" = true ] || [ ! -x "$exe" ]; then
    "$scripts/build-unix.sh" --runtime "$runtime" --output "$output"
fi

running="$(running_pids "$exe" | tr '\n' ' ')"
[ -z "${running// /}" ] || die "mailnix is already running from $output (process ${running% })."

# The browser needs a concrete host, not a wildcard binding.
browser_url="${urls%%;*}"
browser_url="$(printf '%s' "$browser_url" | sed -E 's/^[[:space:]]+|[[:space:]]+$//g; s#://(0\.0\.0\.0|\*|\+|\[::\])#://localhost#')"

open_browser() {
    if command -v open >/dev/null 2>&1 && [ "$(uname -s)" = Darwin ]; then
        open "$1"
    elif command -v xdg-open >/dev/null 2>&1; then
        xdg-open "$1" >/dev/null 2>&1
    else
        echo "Open $1 in your browser."
    fi
}

if [ "$browser" = true ]; then
    if command -v curl >/dev/null 2>&1; then
        healthy() { curl -fsS -o /dev/null --max-time 2 "$browser_url/healthz" 2>/dev/null; }
    elif command -v wget >/dev/null 2>&1; then
        healthy() { wget -q -O /dev/null -T 2 "$browser_url/healthz" 2>/dev/null; }
    else
        echo "Neither curl nor wget found; open $browser_url in your browser once mailnix has started."
        browser=false
    fi
fi

if [ "$browser" = true ]; then
    # mailnix replaces this shell below (exec keeps the PID), so the watcher stops when mailnix exits.
    app_pid=$$
    (
        for _ in $(seq 120); do
            kill -0 "$app_pid" 2>/dev/null || exit 0
            if healthy; then
                open_browser "$browser_url" || true
                exit 0
            fi
            sleep 0.5
        done
    ) &
fi

if [ -t 1 ]; then
    printf '\033[36mStarting mailnix on %s (Ctrl+C to stop)\033[0m\n' "$urls"
else
    printf 'Starting mailnix on %s (Ctrl+C to stop)\n' "$urls"
fi
cd "$output"
exec "$exe" --urls "$urls"
