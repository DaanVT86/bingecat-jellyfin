#!/usr/bin/env bash
set -euo pipefail

framework="${1:-}"
case "$framework" in
net8.0) abi="10.10.7.0" ;;
net9.0) abi="10.11.8.0" ;;
net10.0) abi="12.2.0.0" ;;
*)
	echo "usage: $0 net8.0|net9.0|net10.0" >&2
	exit 2
	;;
esac

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
plugin_root="$(cd -- "$script_dir/.." && pwd)"
project="$plugin_root/Jellyfin.Plugin.BingeCat/Jellyfin.Plugin.BingeCat.csproj"
publish_dir="$plugin_root/artifacts/$framework/publish"
zip_path="$plugin_root/artifacts/BingeCat-Sync-$framework.zip"

dotnet publish "$project" --configuration Release --framework "$framework" --output "$publish_dir"
# Jellyfin otherwise scans every DLL, including native Windows SQLite binaries.
cat > "$publish_dir/meta.json" <<EOF
{
  "guid": "832bd9b3-75aa-4dd7-8477-8d13de65f9ee",
  "name": "BingeCat Sync",
  "version": "0.2.0.0",
  "targetAbi": "$abi",
  "assemblies": [
    "Jellyfin.Plugin.BingeCat.dll",
    "Microsoft.Data.Sqlite.dll",
    "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.provider.e_sqlite3.dll",
    "SQLitePCLRaw.batteries_v2.dll"
  ]
}
EOF
(
	cd "$publish_dir"
	zip -FSr "$zip_path" \
		meta.json \
		Jellyfin.Plugin.BingeCat.dll \
		Microsoft.Data.Sqlite.dll \
		SQLitePCLRaw.core.dll \
		SQLitePCLRaw.provider.e_sqlite3.dll \
		SQLitePCLRaw.batteries_v2.dll \
		runtimes
)
echo "$zip_path"
