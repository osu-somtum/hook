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

  Two more for today's maps: builds before BeatmapSetID existed (b452 to b20120916) read it, so
  osu!direct shows maps from elsewhere as downloaded; and song select (b699 to b1218) changes the
  song when the next map is in another folder, as today's maps all call their audio `audio.mp3`.
- **Screen mode (b476 to 2015).** Going back to windowed puts back the desktop's own screen mode,
  not the size the build saw at startup (with today's display scaling, not a mode the screen has),
  and a refused mode doesn't show "Unable to process your request" boxes.
- **Relax and Autopilot**, as [osu-somtum-patcher](https://github.com/osu-somtum/osu-somtum-patcher)
  does for today's osu!: misses show their X (from b639), a broken combo plays its sound, and the
  results screen keeps the play as a local score like any other (from b699). The HUD shows as in
  any play (from b394a): score, accuracy, combo, HP bar, progress, the leaderboard during the play
  and taiko's score popups. b452 to b1844 submit
  Relax/Autopilot plays (their ranked mods check refused them; later builds do already), for the
  server's Relax and Autopilot leaderboards. Builds before b20130329 send the mods chosen with the
  leaderboard request (`&mods=`), so song select shows the Relax or Autopilot board.
- **Chat links (b337 to b20121223).** `https://` links in chat are clickable and open as they are
  (b639 to b1844 opened them as `http://https://...`); in-game beatmap links match `https://` too.
- **No updater.** The 2014-15 updater's check answers "no update": the server's list would replace
  the build with today's osu!, and failing checks make the game repair itself.
- **No self-update.** Releases are on osu-somtum/hook.

Each of these is found in the client by what its code does (strings, called methods, the shape of
its IL), not by build, so one hook works for every build. They're the same changes the somtum
patcher makes to the decompiled clients (osu-somtum/decompiled-osu-client, steps 6 to 9).

## Supported clients

Any osu! (stable) client released between 2008 and 2015.

> [!CAUTION]
> It isn't compatible with the osu!auth anti-cheat used since 2021: the game closes if you try.

## Usage

Put `osu!somtum.exe` in the osu! folder, next to `osu!.exe`, and run it. It works out which .NET
Framework the build is for and starts the hook made for it (`osu!somtum.net20.exe` or
`osu!somtum.net40.exe`, written next to it as hidden files). A configuration file (`osu!somtum.cfg`)
is created on the first run.

> [!TIP]
> osu! versions before 2015 ran on .NET Framework 2.0, and the 2015 cuttingedge builds on .NET
> Framework 4. `osu!somtum.exe` needs .NET Framework 4 (built into Windows 8 and later). .NET 2.0
> builds use the .NET 2.0 hook when .NET Framework 3.5 is installed, and otherwise run on .NET 4;
> run `osu!somtum.exe --net40` to use .NET 4 for them anyway.

### Configuration

`ServerName` is the server's domain (`blueskychan.dev`). `FixBeatmaps`, `DisableUpdater`,
`FixScreenMode` and `RelaxFixes` turn the beatmap, updater, screen mode and Relax/Autopilot patches
off. The rest you most likely don't need to touch.

> [!TIP]
> The IP address for bancho in clients that use TCP (b20130815 and older) is the first DNS A record
> of the `server` subdomain, e.g. `server.blueskychan.dev`.

## Building a self-contained release from source

- Clone the repository including submodules
- Build the solution (or `TitanicHook.Launcher`) in the Release configuration
- `TitanicHook.Launcher/bin/Release/net40/osu!somtum.exe` is the release: both hooks
  (`TitanicHook.Loader`'s `osu!somtum_merged.exe` for net20 and net40, every dependency built in)
  are inside it

## Developing

- (Optional) Attach your IDE's debugger to osu!.exe. This will allow you to set breakpoints while debugging the hook
- Run TestInjector, which will inject the hook into osu!

## License

GNU GPLv3 or later (see `LICENSE.txt`). Based on Titanic! Hook by Oreeeee.
