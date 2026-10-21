# BingeCat Sync for Jellyfin

Version 0.2.0.

This plugin pairs one local Jellyfin user with one active BingeCat profile and
pushes watched-state changes and full library snapshots to BingeCat over
outbound HTTPS. BingeCat never connects to the Jellyfin server, so private LAN
addresses, local HTTP, and self-signed certificates on Jellyfin remain usable.

## Pairing

1. Install the build matching the Jellyfin server version and restart Jellyfin.
2. Open **Dashboard → Plugins → BingeCat Sync**.
3. Choose a Jellyfin user and select **Pair selected user**.
4. Open the BingeCat verification link and approve the code while the intended
   BingeCat profile is active.
5. Keep the outbound service enabled. The initial full snapshot starts
   automatically; later user-data changes are persisted in the local SQLite
   outbox before delivery.

Pair every Jellyfin user separately. The client never sends a BingeCat user id
or display name as an authority signal; the browser approval supplies the
active profile binding.

## Compatibility and builds

- `net8.0`: Jellyfin 10.10.7 (`build.lts.yaml`)
- `net9.0`: Jellyfin 10.11.8 (`build.yaml`)
- `net10.0`: Jellyfin 12.2 (`build.v12.yaml`)

Run `bash scripts/package.sh net8.0`, `net9.0` or `net10.0` from this
directory. The resulting ZIP is written under `artifacts/` and contains
the plugin DLL plus the SQLite runtime dependencies needed by the local durable
outbox.

The ZIP includes `meta.json` with an explicit managed-assembly list. Keep this
file when installing: it prevents Jellyfin from loading native Windows SQLite
DLLs as managed assemblies. All native runtimes remain bundled for cross-platform
installs. Check built artifacts with `python3 scripts/check-package.py artifacts/*.zip`.

The source is kept in the BingeCat monorepo while the beta contract is being
stabilized. Release publishing should move this directory unchanged into the
dedicated `bingecat-jellyfin` repository referenced by the BingeCat settings UI.

## Privacy and diagnostics

Only explicitly paired local users are synced. The pairing request sends the
Jellyfin server id and version, a random plugin-instance id, and the local
Jellyfin user id and user name. Each event and snapshot item sent to BingeCat
contains the item title, its Jellyfin item id (and series id for episodes),
IMDb/TMDB/TVDB ids, season and episode number, watched state, play count,
last-played time, playback position and runtime. Playback-progress ticks are
not sent; playback start and stop, played/unplayed toggles, ratings and
imports are.

The local SQLite outbox (`outbox.sqlite3` in the plugin data folder) stores
those same events keyed by the local Jellyfin user id. Delivered events are
deleted as soon as BingeCat accepts them; events BingeCat rejects are kept in a
local dead-letter table for diagnostics. Disconnecting a user, or pairing that
user again, deletes all of that user's pending and dead-lettered events, so
history from one pairing is never sent under another.

Raw device tokens remain in the Jellyfin plugin configuration and are never
shown again by BingeCat. The settings UI's **Copy diagnostics** action omits
tokens, titles, server URLs, and local user ids.
