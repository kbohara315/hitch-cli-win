# Install Hitch on Linux x64

Hitch's first Linux release supports x86_64 Linux and Bash. Install `bash`, `stty` (from coreutils), `tar`, `curl`, and `sha256sum` (from coreutils). No administrator privileges or .NET runtime are needed.

```bash
curl -fsSL https://raw.githubusercontent.com/kbohara315/hitch-cli-win/main/scripts/install.sh | bash
```

The installer verifies the release archive checksum, installs `hitch` and the `forkpty` helper under `~/.local/lib/hitch`, and runs `hitch setup --yes` to add a Bash function in `~/.bashrc`. Existing profile and executable files are backed up before updates. Ensure `~/.local/bin` is on `PATH`, then restart Bash or source `~/.bashrc`.

Start a shared Bash session with `hitch` (or `hitch on`). The Bash wrapper returns to the invoking shell and restores its final working directory. While the session is running, use `hitch status`, `hitch context`, `hitch capture`, `hitch send-keys`, and `hitch off` from another shell owned by the same user.

Active session data and Unix-domain sockets are stored in `$XDG_RUNTIME_DIR/hitch/sessions` when available, otherwise `$XDG_STATE_HOME/hitch/sessions`, or `~/.local/state/hitch/sessions`. Hitch creates its session directories with owner-only access. The PTY requires `forkpty` from libutil (provided by standard Linux development environments at build time); the published archive includes the native helper.

Build and test on Ubuntu with:

```bash
dotnet test Hitch.sln -c Release
dotnet publish src/Hitch/Hitch.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/linux-x64
```
