#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
    echo "Hitch currently supports Linux x86_64." >&2
    exit 1
fi
command -v curl >/dev/null || { echo "Install curl first." >&2; exit 1; }
command -v sha256sum >/dev/null || { echo "Install coreutils (sha256sum) first." >&2; exit 1; }

version_url="https://github.com/kbohara315/hitch-cli-win/releases/latest/download/hitch-linux-x64.tar.gz"
checksum_url="${version_url}.sha256"
temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT
curl -fsSL "$version_url" -o "$temporary_directory/hitch-linux-x64.tar.gz"
curl -fsSL "$checksum_url" -o "$temporary_directory/hitch-linux-x64.tar.gz.sha256"
(cd "$temporary_directory" && sha256sum -c hitch-linux-x64.tar.gz.sha256)
tar -xzf "$temporary_directory/hitch-linux-x64.tar.gz" -C "$temporary_directory"
install_root="${HOME}/.local/lib/hitch"
mkdir -p "$install_root"
install -m 0755 "$temporary_directory/hitch" "$install_root/hitch"
install -m 0755 "$temporary_directory/hitch-pty" "$install_root/hitch-pty"
"$install_root/hitch" setup --yes
echo "Hitch installed. Ensure ~/.local/bin is on PATH, then restart Bash."
