# BingeCat Sync for Jellyfin

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

- `net8.0`: Jellyfin 10.10.7
- `net9.0`: Jellyfin 10.11.8

Run `bash scripts/package.sh net8.0` or `bash scripts/package.sh net9.0` from
this directory. The resulting ZIP is written under `artifacts/` and contains
the plugin DLL plus the SQLite runtime dependencies needed by the local durable
outbox.

The source is kept in the BingeCat monorepo while the beta contract is being
stabilized. Release publishing should move this directory unchanged into the
dedicated `bingecat-jellyfin` repository referenced by the BingeCat settings UI.

## Privacy and diagnostics

The outbox contains only provider ids, watched state, playback position, and
timestamps for explicitly paired local users. Raw device tokens remain in the
Jellyfin plugin configuration and are never shown again by BingeCat. The
settings UI's **Copy diagnostics** action omits tokens, titles, server URLs, and
local user ids.
