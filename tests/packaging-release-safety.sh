#!/usr/bin/env bash
set -euo pipefail

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
scratch=$(mktemp -d)
trap 'rm -r -- "$scratch"' EXIT
project="$scratch/project"
fake="$scratch/bin"
destination="$scratch/destination"
export TMPDIR="$scratch/tmp"
mkdir -p "$project/packaging/udev" "$project/assets" "$fake" "$destination" "$TMPDIR"
cp "$repo/Directory.Build.props" "$repo/LICENSE" "$project/"
cp "$repo/packaging/"{build-release.sh,README.txt,io.github.codingncaffeine.ViperSynapse.desktop,viper-synapse-uinput.conf} "$project/packaging/"
cp "$repo/packaging/udev/70-viper-synapse.rules" "$project/packaging/udev/"
cp "$repo/assets"/icon-*.png "$project/assets/"
for name in App Service Cli Hardware Core; do
    mkdir -p "$project/src/ViperSynapse.$name/"{bin,obj}
    touch "$project/src/ViperSynapse.$name/"{bin,obj}/keep
done
touch "$destination/keep"
ln -s "$destination" "$scratch/output"

# Block any unexpected cleanup before it can touch a directory.
cat > "$fake/rm" <<'STUB'
#!/usr/bin/env bash
for path in "$@"; do
    case "$path" in
        -*|"$TMPDIR"/viper-release.*) ;;
        *) printf 'Unexpected cleanup target: %s\n' "$path" >&2; exit 97 ;;
    esac
done
exec /usr/bin/rm "$@"
STUB
cat > "$fake/dotnet" <<'STUB'
#!/usr/bin/env bash
set -eu
while [[ "$1" != -o ]]; do shift; done
output="$2"
mkdir -p "$output"
printf 'CompiledAvaloniaXaml\n' > "$output/viper-synapse.dll"
touch "$output/viper-synapse" "$output/viper-synapsed" "$output/viper-cli"
chmod +x "$output/viper-synapse" "$output/viper-synapsed" "$output/viper-cli"
STUB
cat > "$fake/dpkg-deb" <<'STUB'
#!/usr/bin/env bash
set -eu
[[ "$1" == --build && "$2" == --root-owner-group ]]
sed -n 's/^Version: //p' "$3/DEBIAN/control" > "$4"
STUB
chmod +x "$fake/"{rm,dotnet,dpkg-deb}

PATH="$fake:$PATH" VIPER_OUT="$scratch/output" VIPER_VERSION=0.1.2-rc.3 bash "$project/packaging/build-release.sh"
test -L "$scratch/output"
test -f "$destination/keep"
for name in App Service Cli Hardware Core; do
    test -f "$project/src/ViperSynapse.$name/bin/keep"
    test -f "$project/src/ViperSynapse.$name/obj/keep"
done
test -f "$destination/ViperSynapse-0.1.2-rc.3-linux-x64.tar.gz"
test "$(cat "$destination/viper-synapse_0.1.2-rc.3_amd64.deb")" = "0.1.2~rc.3"
test -z "$(find "$TMPDIR" -mindepth 1 -maxdepth 1 -print -quit)"
