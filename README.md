# TofuTracker for Jellyfin

A Jellyfin plugin that sends what you watch to your [TofuTracker](https://tofutracker.com) library. Play a movie or an
episode (anime included) and it shows up as watched, with no buttons to press.

- Movies and TV episodes, tracked while you play: start, pause, progress and finish.
- Things you mark as played by hand in Jellyfin are synced too.
- Every Jellyfin user links their own TofuTracker account. Nobody's watching ends up in someone else's library.
- You link a user with a short code you approve on tofutracker.com. No passwords or API keys are typed into Jellyfin.

Works with Jellyfin **12.x**. It was built and checked against 12.1.

## Install

1. In Jellyfin open **Dashboard > Plugins > Repositories > +** and add:

   ```
   https://raw.githubusercontent.com/FredMT/jellyfin-plugin-tofutracker/manifest/manifest.json
   ```

   Name it anything, for example "TofuTracker".
2. Open **Dashboard > Plugins > Catalog**, find **TofuTracker** (category General) and install the latest version.
3. Restart Jellyfin.

### Install by hand instead

Download `tofutracker_<version>.zip` from the [releases page](https://github.com/FredMT/jellyfin-plugin-tofutracker/releases),
unzip it into a folder named `TofuTracker_<version>` inside Jellyfin's `plugins` folder, then restart Jellyfin. The folder
must contain `Jellyfin.Plugin.TofuTracker.dll` and `meta.json`.

| System  | Jellyfin's program data folder (the plugins folder is inside it)    |
| ------- | -------------------------------------------------------------------- |
| macOS   | `~/Library/Application Support/jellyfin/`                            |
| Linux   | `/var/lib/jellyfin/` (distribution packages)                         |
| Windows | `%ProgramData%\Jellyfin\Server\`                                     |
| Docker  | `/config/` inside the container                                      |

So on a Mac the folder is `~/Library/Application Support/jellyfin/plugins/TofuTracker_1.0.0.0/`.

## Link a Jellyfin user to a TofuTracker account

Linking is done by a Jellyfin administrator, one user at a time.

1. Open **Dashboard > Plugins > TofuTracker**.
2. Click **Link** next to the Jellyfin user. A code such as `ABCD-EFGH` and a link appear.
3. The TofuTracker account owner opens the link (`https://tofutracker.com/link?code=ABCD-EFGH`), signs in to TofuTracker
   and approves it. If that is not you, send them the link.
4. The page switches to "Linked to <name>". You can close the page at any time while waiting: the server keeps checking
   until the code is approved, denied or expires (10 minutes).

Repeat for every Jellyfin user who should be tracked. Users who are not linked are never sent anywhere.

To stop tracking a user, click **Unlink**. That removes the token from your Jellyfin server. To cut it off on the
TofuTracker side as well, remove the connection under **Settings > Scrobbling** on tofutracker.com.

## Get good matches: use the TheTVDB metadata plugin

TofuTracker identifies titles by their provider ids (IMDb, TMDb, TheTVDB, and AniDB, AniList, MyAnimeList or Kitsu for
anime), never by title text. Jellyfin only knows those ids if a metadata plugin supplied them.

Install the **TheTVDB** plugin from the Jellyfin catalog, enable it as a metadata downloader for your TV and anime
libraries, and refresh the metadata once (**Replace all metadata**). Episodes then carry their TheTVDB ids, which TofuTracker
resolves exactly.

With TMDb alone the plugin still works, but season and episode numbers can only be matched on a best-effort basis, and
long-running anime may end up in your TofuTracker review queue. A network that blocks `api.themoviedb.org` leaves you with
only IMDb ids, which is the weakest case.

## What it does while you watch

| In Jellyfin                              | Sent to TofuTracker                                                          |
| ---------------------------------------- | ---------------------------------------------------------------------------- |
| You press play                           | `start`                                                                      |
| You keep playing                         | `progress`, at most every 30 seconds                                         |
| You pause or resume                      | `pause` / `progress`, immediately                                            |
| You stop before the end                  | `stop` with the position                                                     |
| Jellyfin counts the play as finished     | `watched`                                                                    |
| You mark an item played yourself         | `watched` flagged manual (marking a season or series sends each episode)     |

Whether a play counts as watched in your TofuTracker library is decided by TofuTracker (currently at 80 % progress, or
when Jellyfin reports the play as finished). Marking something as unplayed in Jellyfin is not synced, and nothing from before
you installed the plugin is imported.

A manual "mark played" that happens right after a real play of the same thing (Jellyfin does this for alternate versions of
a video, and some apps do it too) is ignored so it is not counted twice.

## What is sent

For each event, to `https://scrobble.tofutracker.com/v1/events` over HTTPS with that user's token:

- the title as shown in Jellyfin, and its provider ids: for an episode the ids of the series plus the episode's own ids,
  the season and episode number
- the action, the time, the playback position and the length
- Jellyfin's playback session id (so repeated events for one play are not counted twice)
- the plugin name and version and the Jellyfin version (for example "Jellyfin 12.1")

When you link a user it also sends a label like `Living room / alice` (your server's name and the Jellyfin user name), so you
can tell your connections apart in TofuTracker.

**Not sent:** file names or paths, library names, your server's address, Jellyfin user ids, passwords, or anything about
music, photos, books or live TV. Items that have no provider id at all are skipped entirely (the admin page lists them under "Not sent"). TofuTracker's servers do see the
IP address your Jellyfin server connects from, as any web service does.

## What is stored on your server

| File (inside the program data folder)    | What                                                                            |
| ---------------------------------------- | ------------------------------------------------------------------------------- |
| `data/tofutracker/links.json`            | The linked users and their connection tokens. Only readable by the Jellyfin user (mode 600). Never shown in the admin page. |
| `data/tofutracker/pending-watched.json`  | Finished plays that could not be delivered yet (for example while TofuTracker is unreachable). At most 1000, removed once delivered. |
| `plugins/configurations/Jellyfin.Plugin.TofuTracker.xml` | The scrobbler address setting. No secrets. |

Uninstalling the plugin does not delete these files. Delete the `data/tofutracker` folder to remove the tokens.

## Troubleshooting

**Nothing shows up in TofuTracker.**
Check, in this order: the user is linked (Dashboard > Plugins > TofuTracker says "Linked to ..."); the title has provider ids
(open the item, **Edit metadata**, and look for IMDb, TMDb or TheTVDB ids; see the TheTVDB section above); the page
shows `0 event(s) waiting` and a recent delivery time. If it says `last error`, the message tells you what is wrong.

A title that was played but skipped shows up under **Not sent** below the users list on the same page (the last 20 since
the server started) with the reason, and is written once to the Jellyfin log at `Information` level:
`TofuTracker is not sending '<title>': it has no provider ids ...`. Refresh or identify its metadata and play it again.

**"Link rejected by TofuTracker: link again."**
TofuTracker no longer accepts that user's token, usually because the connection was removed in TofuTracker's settings. Click
**Link again**. Events that were waiting for that user are dropped.

**"The code expired."**
Codes last 10 minutes. Click **Link** again to get a new one.

**Linking fails with "Could not reach TofuTracker".**
The Jellyfin server needs outbound HTTPS to `scrobble.tofutracker.com`. Check the server's DNS, firewall and proxy.

**A whole season shows up at once.**
Marking a season or a series as played in Jellyfin marks every episode, and each one is sent, as intended. They arrive as
manual marks dated that day.

**An anime episode lands in the review queue.**
Anime that Jellyfin knows only by TMDb or IMDb ids cannot be matched exactly. Use the TheTVDB plugin, or add the
AniDB / AniList plugins, and refresh the metadata.

**I need more detail.**
The plugin logs under the name `Jellyfin.Plugin.TofuTracker`. Set that category to `Debug` in Jellyfin's logging
configuration to see each event it queues. Tokens are never logged.

## Limits

- Jellyfin 12 only (the plugin targets ABI 12.0.0.0). Older servers are not supported.
- Only an administrator can link users. The approval itself is done by the TofuTracker account owner.
- A multi-episode file (S01E01-E02) is sent as its first episode.
- Un-marking an item is not synced, and there is no import of old watch history.

## Build from source

You need Docker only (the .NET 10 SDK runs in a container).

```bash
scripts/dn.sh build -c Release     # build
scripts/dn.sh test -c Release      # unit tests
scripts/build-zip.sh               # plugin zip in artifacts/ (DLL + meta.json)
scripts/verify-against-server.sh /Applications/Jellyfin.app/Contents/MacOS   # compile against a real server install
```

With the SDK installed locally, plain `dotnet build -c Release` and `dotnet test` do the same.

How it is put together:

- `src/Jellyfin.Plugin.TofuTracker/Core` has no dependency on Jellyfin types. It holds the event contract, item mapping,
  the playback planner (session ids, 30 second throttle, de-duplication), the token store, the bounded outbound queue, the
  HTTP sender with backoff and the device-code pairing client.
- `src/Jellyfin.Plugin.TofuTracker/Playback` is the only code that reads Jellyfin entities and listens to
  `ISessionManager` and `IUserDataManager`.
- `src/Jellyfin.Plugin.TofuTracker/Api` and `Configuration` are the admin API (administrators only) and the settings page.
- `tests/` has the xUnit tests: mapping, planner, queue, sender, pairing, controller and the hosted service driven through
  substitutes of Jellyfin's managers.

## Releasing

Everything runs on a machine with Docker, `gh` and Python:

```bash
scripts/dn.sh test -c Release                 # tests must be green
scripts/build-zip.sh                          # artifacts/tofutracker_<version>.zip (DLL, tofutracker.png, meta.json)
gh release create v<version> artifacts/tofutracker_<version>.zip --title v<version>
GH_TOKEN="$(gh auth token)" scripts/update-manifest.sh v<version> FredMT/jellyfin-plugin-tofutracker
```

`update-manifest.sh` adds the version (checksum, download URL of the release asset, icon) to `manifest.json` on the
`manifest` branch, which is the repository URL in the install steps. It needs [jprm](https://github.com/oddstr13/jellyfin-plugin-repository-manager)
(`pip install jprm`). Keep `version` in `build.yaml` and `Directory.Build.props` in step.

## License

GPL-3.0-only. See [LICENSE](LICENSE).
