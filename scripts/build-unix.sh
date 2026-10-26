#!/usr/bin/env bash
# Builds mailnix as a standalone Linux or macOS app: one self-contained executable (no .NET installation needed)
# with the web UI next to it.
#
#   1. Frontend: npm ci, type-check + build, license gate, third-party notices.
#   2. Backend: self-contained single-file publish for the given runtime, with the SPA in wwwroot.
#   3. Output: <output>/mailnix, wwwroot/, appsettings.json, LICENSE, README.txt (and a .tar.gz with --archive).
#      A data/ folder already in <output> is kept on rebuilds and never put into the archive.
#
# The app stores its database and encryption keys in data/ next to the executable and listens on
# http://localhost:5000 unless started with --urls (see README.txt in the output).
#
# Examples:
#   ./scripts/build-unix.sh
#   ./scripts/build-unix.sh --runtime linux-arm64 --archive

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: build-unix.sh [options]

  --runtime <rid>        linux-x64, linux-arm64, linux-musl-x64, linux-musl-arm64, osx-x64 or osx-arm64
                         (default: this machine)
  --output <dir>         default: artifacts/<linux|macos>/<rid> in the repository
  --archive              also create <output>.tar.gz
  --skip-license-check   skip the frontend license gate (e.g. offline experiments); not for release builds
  -h, --help             show this help
EOF
}

die() { printf 'error: %s\n' "$*" >&2; exit 1; }

step() {
    if [ -t 1 ]; then printf '\033[36m==> %s\033[0m\n' "$1"; else printf '==> %s\n' "$1"; fi
}

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
        *) die "unsupported OS '$os'; pass --runtime (on Windows use build-windows.ps1)" ;;
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
archive=false
skip_license_check=false
while [ $# -gt 0 ]; do
    case "$1" in
        --runtime) [ $# -ge 2 ] || die "$1 needs a value"; runtime="$2"; shift 2 ;;
        --output) [ $# -ge 2 ] || die "$1 needs a value"; output="$2"; shift 2 ;;
        --archive) archive=true; shift ;;
        --skip-license-check) skip_license_check=true; shift ;;
        -h | --help) usage; exit 0 ;;
        *) usage >&2; die "unknown option '$1'" ;;
    esac
done

[ -n "$runtime" ] || runtime="$(detect_runtime)"
case "$runtime" in
    linux-x64 | linux-arm64 | linux-musl-x64 | linux-musl-arm64) platform=linux ;;
    osx-x64 | osx-arm64) platform=macos ;;
    *) die "unsupported runtime '$runtime'" ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
frontend="$root/frontend"
api="$root/backend/src/Mailnix.Api"
[ -n "$output" ] || output="$root/artifacts/$platform/$runtime"
mkdir -p "$output"
output="$(cd "$output" && pwd)"

for tool in dotnet npm; do
    command -v "$tool" >/dev/null 2>&1 || die "'$tool' was not found on PATH"
done

# --- Frontend ----------------------------------------------------------------------------------------
(
    cd "$frontend"
    step 'Installing frontend packages'
    npm ci --no-audit --no-fund
    step 'Building the frontend'
    npm run build
    if [ "$skip_license_check" = false ]; then
        step 'Checking frontend licenses'
        npm run license:check
    fi
    step 'Writing third-party notices'
    npm run license:notices
)

# The API serves the SPA from its wwwroot (git-ignored build output).
rm -rf "$api/wwwroot"
cp -R "$frontend/dist" "$api/wwwroot"

# --- Backend -----------------------------------------------------------------------------------------
running="$(running_pids "$output/mailnix" | tr '\n' ' ')"
[ -z "${running// /}" ] || die "mailnix is running from $output (process ${running% }). Stop it first."
# Replace the previous build, but keep its data folder (database, keys): it may be a local install in use.
find "$output" -mindepth 1 -maxdepth 1 ! -name data -exec rm -rf {} +

step "Publishing the backend ($runtime, self-contained)"
dotnet publish "$api/Mailnix.Api.csproj" \
    --configuration Release \
    --runtime "$runtime" \
    --self-contained true \
    --output "$output" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=None \
    -p:OpenApiGenerateDocuments=false \
    -p:IsTransformWebConfigDisabled=true \
    -p:MailnixRuntimeLockFiles=true

mv "$output/Mailnix.Api" "$output/mailnix"
chmod +x "$output/mailnix"
# Development settings are not part of a release.
rm -f "$output/appsettings.Development.json"
cp "$root/LICENSE" "$output/"

if [ "$platform" = macos ]; then
    quarantine_note='
A downloaded archive is quarantined by macOS; unpack it and allow the app with
    xattr -dr com.apple.quarantine <folder>
'
else
    quarantine_note=''
fi

cat >"$output/README.txt" <<EOF
mailnix $runtime
$(printf '%*s' "$((8 + ${#runtime}))" '' | tr ' ' '=')

Start ./mailnix and open http://localhost:5000 in your browser.
The first account you register becomes the administrator.
$quarantine_note
Data (SQLite database, encryption keys for stored mailbox passwords) is kept in the
'data' folder next to the mailnix executable. Back it up, and keep it when updating:
replace everything else with the new version.

Without a master key, the encryption keys are stored unencrypted in the database and
mailnix logs a warning at startup. Create one before the first start, keep it outside
the data folder and back it up separately:
    ./mailnix --generate-master-key > ~/.mailnix-master.key
    chmod 600 ~/.mailnix-master.key
    Mailnix__DataProtection__MasterKeyFile=~/.mailnix-master.key ./mailnix

Other address or port:
    ./mailnix --urls http://localhost:8080
Reachable from other computers (put a TLS reverse proxy in front for anything public):
    ./mailnix --urls http://0.0.0.0:8080

Settings can be changed in appsettings.json or with environment variables, e.g.
    Mailnix__Auth__AllowRegistration=false ./mailnix
All settings are listed in the project README, section "Configuration".

License: MIT (LICENSE). Third-party notices: wwwroot/THIRD-PARTY-NOTICES.md.
EOF

if [ "$platform" = macos ] && [ "$(uname -s)" != Darwin ]; then
    echo "Note: built on $(uname -s), so the executable is not code-signed. Apple Silicon Macs only run signed" \
        "executables; sign it ad hoc on the Mac with: codesign --force --sign - mailnix" >&2
fi

if [ "$archive" = true ]; then
    name="$(basename "$output")"
    tarball="$output.tar.gz"
    rm -f "$tarball"
    # Never ship a local data folder: it holds the database and the keys for stored mailbox passwords.
    tar -czf "$tarball" -C "$(dirname "$output")" --exclude="$name/data" "$name"
    echo "Archive: $tarball"
fi

size_kb="$(du -sk "$output" | cut -f1)"
if [ -d "$output/data" ]; then size_kb=$((size_kb - $(du -sk "$output/data" | cut -f1))); fi
if [ -t 1 ]; then
    printf '\033[32mDone: %s (%d MB)\033[0m\n' "$output" "$((size_kb / 1024))"
else
    printf 'Done: %s (%d MB)\n' "$output" "$((size_kb / 1024))"
fi
