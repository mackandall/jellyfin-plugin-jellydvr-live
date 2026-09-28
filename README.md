# JellyDVR Live (Jellyfin plugin)

A SageTV-style live TV buffer for Jellyfin 12. While someone watches a channel, the programme on it is
recorded from the moment they tuned in, as an ordinary Jellyfin recording that shares their tuner stream.
Pausing stays on the live stream as usual; the buffer is for:

- rewinding past the point where you tuned in,
- carrying on from another device (pause on the TV, resume on the laptop),
- skipping commercials while catching up (the Comskip plugin scans recordings in progress),
- keeping the programme as a real recording from the moment you tuned in.

Part of the JellyDVR set alongside Comskip Commercial Segments, Episode History, JellyDVR Space and XMLTV
Guide Cleaner.

## How it works

- Watches playback of live channels. For each channel being watched, the current programme is recorded by a
  one-off Live TV timer (no padding). Shortly before a programme ends, the next one's timer is set, so the
  buffer carries on across the boundary.
- If the programme is already being recorded by a scheduled timer, that recording is used as the buffer and
  never touched.
- A while after the last viewer leaves (default 10 minutes), buffering that channel stops.
- Buffer recordings are tagged `Live buffer` until kept. Episode History ignores them and JellyDVR Space
  deletes them first when space runs low.
- **Keep** turns a buffer into an ordinary recording: untagged, recorded to the end of the programme, never
  deleted by this plugin.
- Buffers nobody kept are deleted once nobody has watched them for a while (default 4 hours), with their
  sidecars and any season/show folder they left empty.

The buffer list lives at `<data>/jellydvr-live/buffers.json` so the other JellyDVR plugins can read it.

## Settings (Dashboard → Plugins → JellyDVR Live)

- **Buffering** — *From tune-in* (default), *On demand* (only when a viewer asks), or *Off*.
- **Keep buffering after the last viewer leaves** — minutes (default 10).
- **Delete unkept buffers** — hours after they were last watched (default 4).

## API (for the web and TV client add-ons)

- `GET /JellyDvrLive/Channel/{channelId}` — buffers for a channel, oldest first.
- `POST /JellyDvrLive/Channel/{channelId}/Start` — start buffering now (on-demand mode).
- `GET /JellyDvrLive/Item/{itemId}` — the buffer an item belongs to, if any.
- `POST` / `DELETE /JellyDvrLive/Keep/{bufferId}` — keep / un-keep.
- `GET /JellyDvrLive/Status` — everything (admin).

## Install

```
dotnet build -c Release
sudo ./install.sh   # restarts Jellyfin
```
