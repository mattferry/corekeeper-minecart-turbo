# Publishing / updating Minecart Turbo on mod.io

`modio_publish.py` creates-or-updates the mod (game 5289, name_id `minecartturbo`, live id 6350497) and uploads
a new modfile. It reads `MODIO_TOKEN` (set it from the vault's `MODIO_OAUTH_TOKEN` in `modio-api.env`; full-scope
token minted 2026-08-31, expires 2026-11-29, belongs to mod.io account `matthewferry25` 15525914 — NOT Oogley)
and `MODIO_API_URL` (writes must use the u-<id>.modapi.io domain; api.mod.io is deprecated for writes; a browser
User-Agent-less request gets 403 — the script sends its own UA).

To ship an update: bump VERSION in the script, rebuild the zip from the mod folder (ModManifest.json + Scripts/ +
Localization/ at the zip root — NEVER change the manifest guid or the object/clone names), place it next to the
script as MinecartTurbo-<VERSION>.zip, adjust ZIP, run. The logo only needs passing on first creation.
