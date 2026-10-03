# osu!somtum hook

A small executable that connects old osu! clients (2008 to 2015) to an osu!somtum server
(**blueskychan.dev** by default), without any permanent changes to the client. It's
Titanic! Hook by Oreeeee (osu!Titanic) with osu!somtum's changes,
under the same license (GPLv3 or later).

## What it does

- **Connects to the server.** Every host the client talks to goes to the same subdomain of the
  configured server: osu!'s own (`*.ppy.sh`, `peppy.chigau.com`), and those of the private
  servers old builds are handed out patched for (`*.titanic.sh`, `osu.lekuru.xyz`), so Titanic's
  client downloads work too. HTTP bancho's fallback addresses (raw IPs, TCP bancho's port 13381)
  go to `c.<server>`. TCP bancho (builds up to b20130815) connects to `server.<server>`.
- **Plays today's beatmaps.** The client's beatmap loaders read each `.osu`/`.osb` line through
  `Compat/BeatmapCompat.cs`, which rewrites only what that build can't read:
  - decimal HP/CS/OD/AR, rounded (builds before b20140616);
  - perfect-circle sliders as Bezier arcs (before b20121115);
  - osu!mania difficulties as an empty map named "… (mania)" (before b20121003);
  - storyboards: decimal positions rounded, the Overlay layer drawn as Foreground;
  - newer hit object type bits and timing point effects left out.
- **No updater.** The 2014-15 updater's check answers "no update": the server's list would replace
  the build with today's osu!, and failing checks make the game repair itself.
- **No self-update.** Releases are on osu-somtum/hook.

Each of these is found in the client by what its code does (strings, called methods), not by build,
so one hook works for every build.

## Supported clients

Any osu! (stable) client released between 2008 and 2015.

> [!CAUTION]
> It isn't compatible with the osu!auth anti-cheat used since 2021: the game closes if you try.

## Usage

Put the release executable for the client's .NET Framework version in the osu! folder and run it.
A configuration file (`osu!somtum.cfg`) is created on the first run.

> [!TIP]
> osu! versions before 2015 ran on .NET Framework 2.0. Cuttingedge since April 2015 and Stable since
> November 2015 use .NET Framework 4.

### Configuration

`ServerName` is the server's domain (`blueskychan.dev`). `FixBeatmaps` and `DisableUpdater` turn
the beatmap and updater patches off. The rest you most likely don't need to touch.

> [!TIP]
> The IP address for bancho in clients that use TCP (b20130815 and older) is the first DNS A record
> of the `server` subdomain, e.g. `server.blueskychan.dev`.

## Building a self-contained release from source

- Clone the repository including submodules
- Build `TitanicHook.Loader` in the Release configuration
- The build output has an `osu!somtum_merged.exe` with every dependency built in

## Developing

- (Optional) Attach your IDE's debugger to osu!.exe. This will allow you to set breakpoints while debugging the hook
- Run TestInjector, which will inject the hook into osu!

## License

GNU GPLv3 or later (see `LICENSE.txt`). Based on Titanic! Hook by Oreeeee.
