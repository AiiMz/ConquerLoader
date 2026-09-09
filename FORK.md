# What this fork changes

Fork of [OpenConquerOrg/ConquerLoader](https://github.com/OpenConquerOrg/ConquerLoader).
It exists mainly for one reason: **the loader updates the client before it
starts it**, so the patch step cannot be skipped. It also carries the three
fixes below that a server addressed by DNS name needs.

Upstream is otherwise unchanged. There is no rebranding and no UI
surgery, deliberately — every change below is additive
or a one-line retarget, so pulling upstream fixes stays an ordinary merge:

```
git fetch upstream && git merge upstream/master
```

## Why the patcher is not a separate executable

EternalAbyss shipped a standalone `EternalAbyssPatcher.exe` next to
`ConquerLoader.exe` and nothing made anyone run it. `ConquerLoader.exe` is the
name every 6300 player already knows, it is what the first desktop shortcut
points at, and a player who never runs the patcher plays whatever content they
happen to have — forever. That never surfaces as an error. It surfaces as
missing garments and wrong icons, weeks later, reported as a server bug.

Renaming the two executables around each other was tried first and is a speed
bump at best: the inner exe is still there and still runs. Putting the check
inside the launch path is the only version that cannot be skipped by accident,
and this is the repository that makes it possible — upstream is open source, so
"merge them" is a real option rather than a wrapper.

## The changes

| | |
|---|---|
| `CLCore/Patching/` | **New.** The whole patcher: manifest model and validation, the path guard, the compare step, the atomic installer, the HTTP client, and `ClientPatcher` on top of them |
| `CLCore/Patching/SelfUpdate.cs` | **New.** The loader updating itself from the patch layer, so a loader bug is fixable after players have a copy — below |
| `CLCore/Models/ServerConfiguration.cs` | **One field**, `PatchManifestUrl`. Null or empty — which is what every existing `config.json` has — turns patching off and the loader behaves exactly as it always did |
| `ConquerLoader/Core.cs` | **One method**, `RunAutoPatch`, returning the same `PluginPreLaunchResult` the plugin hook already uses so both launch paths reuse the cancel-launch plumbing |
| `ConquerLoader/Forms/Main.cs`, `Forms/WPF/MainLite.xaml.cs` | **Six lines each**, calling it immediately before `RunPreLaunchPlugins` |
| `CLCore/ClientOptions/` | **New.** `WingVisibility`, the Hide Wings toggle, and `FrameRateLimit`, the frame cap - both below |
| `CLCore/Models/LoaderConfig.cs` | **Two fields**, `HideWings` and `FpsLimit`. Absent from a `config.json` means false and 60, so an existing one gets the cap without being edited |
| `ConquerCipherHook/FpsLimiter.*` | **New.** The frame cap itself, in the hook that is already injected into the client |
| `ConquerLoader/Forms/WPF/MainLite.xaml` + `.xaml.cs` | **Two cards** in the Options panel: Hide Wings, and the frame cap that replaced the FPS Unlock toggle |
| `ConquerLoader/Core.cs` | **One method**, `SafeIO.DiffersFrom`, so a changed hook DLL reaches an install that already has an older one |
| `Tests/CLCore.Tests/` | **New.** 78 tests over the path guard, the manifest validation, the compare-and-install pair, the wing rewrite and the frame cap |
| every `*.csproj` | `TargetFrameworkVersion` v4.6.2 → **v4.8** |
| `ConquerCipherHook.vcxproj` | **`UseOfMfc` Static → false**, plus an explicit `RuntimeLibrary` — below |
| `ConquerLoader/Models/ServersDatGenerator.cs` | **One method**, `ResolveToIPv4`, so `LoginHost` may be a hostname |
| `CLCore/SocketSystem.cs` | **One method**, `CLClient.EnsureReachable`, bounding the CLServer connect |
| `CLCore/Constants.cs` | **One flag**, `EnableCLServerConnections`, now `false` |

### Configuring it

Add `PatchManifestUrl` to the server entry in `config.json`:

```json
{
  "ServerName": "AWS",
  "LoginHost": "play.example.com",
  "PatchManifestUrl": "https://example.com/client/"
}
```

The loader expects `manifest.json` directly under that URL and each file under
`files/<path>`. The document is the one
[EternalAbyssCo](https://github.com/AiiMz/EternalAbyssCo)'s
`Tools/ClientManifest` generates; `schemaVersion` is the contract between them
and a document declaring a version this loader does not know is refused rather
than guessed at.

**It is per server, not global**, because the whole point of this loader is that
one install can point at several of them and a client is only ever in step with
one at a time.

### Hashes, not versions

A version stamp records what *should* be true. A file half-written by a crashed
download, or hand-edited by a curious player, is exactly the case where the
stamp is right and the file is wrong — and it is a case that will happen. So
every file in the manifest is compared by size and then by SHA-256. Hashing 844
files against the real client costs about 3.5 seconds, and it turns "repair my
install" from a support conversation into the normal code path.

The stock `CLAutoPatchPlugin` is a different design — zip packages, applied-once
by signature — and is untouched. Nothing here replaces it.

### What it does on failure, and why the defaults differ

| | |
|---|---|
| The manifest could not be fetched | **Launch anyway.** The patch site being unreachable says nothing about whether the game server is up; usually the website is mid-deploy or the machine is offline. Nothing is known to be wrong with the install |
| A file could not be updated | **Do not launch.** Something *is* known to be wrong, and a client missing art it expects fails in ways that look like a server bug |
| `PatchManifestUrl` is not a valid URL | **Do not launch.** The configuration is wrong rather than the install, and silently not patching is the exact behaviour this fork removes |
| Anything unexpected | **Launch only if nothing had been written yet.** A half-patched install is the case worth stopping for; a failure before the first byte is not |

Every decision is written to `conquerloader.log`, prefixed `[Patch]`.

### What it never does

**Nothing here deletes anything.** The client root holds tens of thousands of
files the manifest says nothing about — `Conquer.exe`, the `.wdf` archives, the
player's own screenshots and their `config.json`. A patcher that removed what it
did not recognise would eat all of it. The cost is that a file dropped from the
patch layer stays on disk forever, which is the right trade: a stale file is
inert, and the alternative failure deletes a player's install.

**Downloads never overwrite in place.** They land in a sibling `.part` file,
hashed while writing rather than read back afterwards, and only then moved. A
failed patch leaves the install exactly as it was.

**The patch target is the loader's own folder**, not the working directory:
manifest paths are relative to the client *root*, and the working directory is
an `Env_DX8` or `Env_DX9` subfolder whenever one of those is in use.

## The loader updates itself

Everything in the patch layer is inert data except one file: the loader. It
shipped only inside the 1.4 GB base archive, so **the newest code in the project
was also the only part of it that could never be fixed after a player had a
copy** — nobody re-downloads 1.4 GB without a reason, and after the first invite
wave the population would have fragmented by loader version permanently. That is
backwards from where the risk is: the content files change weekly and cannot
break a launch.

So `ConquerLoader.exe` is now an entry in the manifest like any other, and the
loader recognises the entry that names the file it is running from.

**A running executable cannot be deleted or overwritten on Windows, but it can
be renamed.** That is the whole mechanism. `FileInstaller` deletes and then
moves, which on the running image is a sharing violation — counted as a file
that could not be updated, which means *do not launch*. So "just add it to the
manifest" is not a no-op that works by luck; it is a client that refuses to
start, on every launch, permanently. `FileInstaller.Stage` is the download and
verification half split out for this: the loader is hashed while it is written
by the same code as every other file, and then `SelfUpdate.Replace` moves the
running image aside and moves the replacement into its name.

**The loader goes first and alone.** A new loader may read a manifest an old one
cannot, so the content comparison belongs to the version that shipped with the
document describing it. A run that replaces the loader touches nothing else and
restarts.

**One hop, enforced by an environment variable.** The restarted process carries
`CONQUERLOADER_SELFUPDATED=1` and will not update itself again. Without it, a
manifest naming a hash the published exe does not actually have would replace,
restart, find itself out of date, and repeat — a launcher that never launches,
on every machine at once, fixable only by a new download. When that happens the
loader says so in the log, patches the content, and lets the player play.

**The superseded image is `ConquerLoader.exe.old`, and the suffix is chosen
rather than invented.** EternalAbyssCo's `ManifestBuilder.IsBackup` already
keeps `*.old` out of the patch layer and `New-ClientArchive.ps1` already cuts it
from the shipped archive, both by the same test — a novel suffix would need both
of them taught about it and would be forgotten in one. It is swept at the *start*
of the next run, because at the end of the run that created it the file is still
mapped by the process doing the sweeping. The sweep is scoped to this image's
own name: deleting `*.old` beside the loader would be deleting a player's files
because they happened to name one the way this names its own.

### What it does on failure

| | |
|---|---|
| The download or the rename failed | **Launch.** The old loader is still the one running and it still works. Refusing to play because an update nobody asked for could not be applied is the wrong trade |
| The rename succeeded and the replacement failed | **The first move is undone.** The install is exactly as it was |
| Both moves failed | **Do not launch**, and say where the working loader is. This is the only outcome here that costs a player anything: nothing is at `ConquerLoader.exe` any more, so it has to be said while there is still a running loader to say it with. `LoaderNotRestoredException` exists to be distinguishable |
| Already restarted once and still out of date | **Launch**, and name the bad publish in the log |

### Rehearsed rather than reasoned about

Against a live process, a real `HttpListener`, and a manifest from the real
`client-manifest`:

| | |
|---|---|
| Renaming a running image | The process kept running and kept its own identity; the replacement took its name |
| The sweep | Refused while the previous image was still mapped, removed it on the next run. This is the measurement the "sweep at the start" design rests on |
| Loader first, alone | A run that replaced the loader left `ini/a.dat` at the old version, and the restarted run patched it |
| The guard | With `CONQUERLOADER_SELFUPDATED` set and the loader still stale, nothing was replaced, nothing restarted, and the launch went ahead |
| The real 45 MB exe | Staged into the real 3,188-file layer by `client-manifest add` and verified against the manifest it produced |

**And it caught a bug that read correctly.** `QuoteArguments` escaped every
backslash, which is what "escape the special characters" looks like — but
`CommandLineToArgvW` treats a backslash as an escape *only* before a quote, so
the restarted loader received `D:\\Some Dir\\Conquer`. Doubling is right for a
run of backslashes before a quote or at the end of a quoted argument, and wrong
everywhere else. Nothing about rereading the method showed it; the first
restarted process did.

## Hide Wings

A per-player cosmetic toggle in the Options panel. It rewrites
`ini\Action3DEffect.ini` immediately before launch, appending `_hidden` to the
414 lines that bind wing art so the effect names miss; unchecking it takes the
suffix off again.

**Nothing is hidden from the server, which is the point.** Wings are worn at
equipment position 19 and carry real battle power. Every server-side attempt at
this had to tell the client something false about what was equipped - blank the
slot, or swap the item id for a look-alike - and the client works its own battle
power out from that same table, so the figure it showed the player dropped while
nothing on the server had moved. Here the server sends the real item, the client
counts it, and the only thing that changes is whether the art can be found. The
server is never told the setting exists.

A key the client looks up and does not find draws nothing, and that is the
client's ordinary behaviour rather than an error path: wings below Super quality
have no binding at all, which is what the item means when it says you must
upgrade it before you will grow a pair.

**It runs after the patch step, always.** The patcher compares by hash and
restores the file the moment it sees the suffix, so running the rewrite first
would simply have it undone a second later. Running it second means the stock
file arrives and the player's choice is re-applied on top of it, every launch,
with no state kept anywhere.

**The suffix is why there is no backup file.** The original effect name stays in
the line, so the edit is reversible from the file alone - nothing to keep in
step, and no list of stock names to go stale when wing art is added. Both
directions are idempotent, a line edited by hand into something unrecognisable
is left alone rather than guessed at, and the rewrite works on bytes so the
file's mixed line endings survive it. That last one matters more than it sounds:
this file is LF throughout with a tail of eighteen CRLF lines, and a rewrite that
normalised them would change its hash and make the patcher re-download it on
every launch.

**It never cancels a launch.** The worst a failure here can do is draw wings a
player asked to hide, or hide wings they asked for. Every outcome is written to
`conquerloader.log`, prefixed `[Wings]`.

## The frame cap

The Options panel used to carry an **FPS Unlock** toggle. It did nothing. The
loader stored it in `config.json` and read it back only to draw the toggle -
nothing downstream ever looked at it, and there was no cap in the client for it
to have removed. It is now `Unlimited`, sitting beside the cap it turns off.

**The cap is not about electricity.** The client has no limiter of its own, so
it draws as fast as the machine allows - 700 FPS on a current GPU - and it
advances animations a step per frame rather than per unit of time. Uncapped, the
whole game runs at ten times the speed it was drawn for. 60 is the default and
30 through 144 are offered; a `config.json` may hold any value between 10 and
1000 and it is honoured, and anything outside that falls back to 60.

`FrameRateLimit.Resolve` is the single place that turns the two settings into
one number, because two launch paths and two settings screens have to agree on
it. The number reaches the client as `MAX_FPS` in `CLHook.ini`, where 0 means no
cap - so an install whose loader predates this reads 0 and behaves exactly as it
always did.

### Where it is enforced

In `ConquerCipherHook`, which the loader already injects into every 6300 client.
`FpsLimiter_Install` waits for `d3d8.dll`, creates a throwaway device, reads slot
15 of its vtable - `IDirect3DDevice8::Present` - and detours the function that
address points at, sleeping in front of it until the frame is due.

**The throwaway device is read for one address and then released; nothing about
its own vtable is patched.** This d3d8.dll gives every device a private copy of
the vtable, allocated a few hundred bytes past the device itself, so overwriting
a slot there would cap that one device and nothing else - which is exactly the
version of this that was written first, and it silently did nothing. What the
copies share is what they point at: slot 15 of a device made by the limiter and
slot 15 of a device made anywhere else hold the same address inside `d3d8.dll`.

Detouring the function rather than the slot also means there is no race to lose
against the client's own graphics startup, and no need to hook
`Direct3DCreate8` and wait to be handed the real device: a device the client
created before the DLL was injected is capped just the same.

### The client presents three times per frame

**A cap on `Present` calls is not a cap on frames, and in this client the two
differ by a factor of three.** Pacing every call gave exactly a third of the
number asked for - 60 became 20 - and the reason is not that the cap missed:
measured in game, uncapped, the client made **2151 `Present` calls a second
while reporting 700 FPS**, and under a 60 cap it made **exactly 60.0 a second
while reporting 19**. The pacing was hitting its target perfectly. The target
was the wrong unit.

The three calls are not alike, which is what makes them separable. The gaps
between them repeat, indefinitely and to three decimal places:

```
0.025 ms    0.162 ms    1.207 ms    (sum 1.394 ms = 717 frames/s)
```

One of the three is the frame - the client thinking and drawing - and the other
two are that same finished frame going out again microseconds later. So a call
is paced when the client did a frame's worth of work in front of it, and the
re-presents go through untouched.

"A frame's worth" is measured against the largest gap seen in the last 32 calls
rather than against a fixed number of milliseconds, because no fixed number is
right for both a client spending 1.2 ms a frame and one spending 20. **Half the
maximum** is the threshold: it separates the observed 1.207 from the 0.162 with
room to spare, and - the part that matters more - it leaves a client that
presents once per frame behaving exactly as it did, because all of its gaps are
the same size and so all of them clear half the maximum. A rule that paced only
the single largest gap would have quietly run such a client at three times its
cap. Gaps over 100 ms are kept out of that history: a map load is not a frame,
and admitting one would raise the maximum so far that nothing cleared half of it
again and the cap would come off until it aged out.

The gap being compared is the client's own - the time between two calls with any
wait this hook injected subtracted back out - so the shape does not change when
a cap is switched on, and the classification holds at 30 FPS as well as at 700.

Both shapes are covered by a harness that replays the measured pattern against
the real limiter: at a cap of 60 the three-present client comes out at 59.8
frames/s (179 presents/s) and a one-present client at 59.6.

### Pacing

The pacing carries its deadline forward by one period per frame rather than
recomputing it from the clock, so a frame that overruns is absorbed by the next
one instead of the cap drifting slow; a stall long enough to owe more than four
frames resyncs instead, so alt-tabbing does not come back to a burst of catch-up
frames. `timeBeginPeriod(1)` goes in with the hook, and the last millisecond
before the deadline is yielded rather than slept, because `Sleep` is only
accurate to the timer period.

Diagnostics go to `OutputDebugStringA` prefixed `[FpsLimiter]` - visible in
DebugView, and nothing on a player's disk. A failure anywhere leaves the client
uncapped, which is where it started.

### Measuring it

`"FpsDebug": true` in `config.json` writes `FPS_DEBUG=1` into `CLHook.ini` and
the hook then appends a block to `CLHook.fps.log` beside `conquer.exe` every ten
seconds: how many times `Present` was called, how those calls were spaced, and
which devices and windows they went to. It installs the hook even with no cap
configured, so the uncapped client can be measured too. There is no UI for it.

It is worth carrying because **the number the client draws on screen is its own
count of rendered frames, and that is not the quantity this paces.** When the
two disagree - a cap of 60 with the client reporting 20 - nothing outside the
process can say which of them is wrong, and the gap list settles it: several
`Present` calls microseconds apart followed by a long pause is one rendered
frame being presented more than once, and the cap is then counting the wrong
thing rather than missing its target.

### The hook does not use MFC, and said it did

`ConquerCipherHook.vcxproj` carried `<UseOfMfc>Static</UseOfMfc>` in both
configurations. Nothing in the project uses MFC — `stdafx.h` includes
`targetver.h` and `windows.h` and nothing else, and no source names an `afx`
header. It is template cruft from whenever the project was created.

It stopped being harmless when `windows-latest` moved to **Visual Studio 18**,
whose image does not carry the MFC libraries:

```
Microsoft.CppBuild.targets(504,5): error MSB8041: MFC libraries are required
for this project. Install them from the Visual Studio installer.
```

A developer machine with the C++ desktop workload has MFC, so this only ever
failed on a runner.

**Turning it off needed a second line, and that is the part worth knowing.**
This project never set `RuntimeLibrary`; it was getting the **static** CRT as a
side effect of `UseOfMfc=Static`. Dropping the MFC setting alone would have
fallen back to the default `MultiThreadedDLL` — and this DLL is injected into
the client on players' machines, so it would then have needed the VC++ 2022
redistributable installed, on a population running a client that ships its own
VC90 runtimes precisely because it cannot assume any. So `RuntimeLibrary` is now
stated outright, `MultiThreaded` for Release and `MultiThreadedDebug` for Debug.

Checked rather than assumed: the shipped `Resources\ConquerCipherHook.dll`
imports `WS2_32`, `USER32`, `WINMM` and `KERNEL32` and no CRT at all, and a
clean-clone build with MFC off imports exactly the same four.

### Why the hook DLL is now overwritten

`GenerateRequiredDLL` used to write the hook DLLs out of the loader resources
only when the file was **absent**, logging "Using existing" otherwise. Every
install that already had a client therefore kept whatever hook it first got,
forever - so a loader shipping a fixed hook would have fixed nothing for anybody
who already plays here. `SafeIO.DiffersFrom` compares the bytes, and
`ConquerCipherHook.dll` is rewritten when they differ.

## Reaching the server by name

These three predate the patcher and were carried over from the fork of
darkfoxdeveloper/ConquerLoader this replaced. Together they are what lets
`LoginHost` be a domain, so the server's IP can change without every player
editing `config.json`.

**The client cannot resolve a hostname.** It validates `ServerIP` as a numeric
address and rejects anything else outright — `ERROR: IP addr too long` from
`3drole/network/socket.h:130` — without ever attempting a lookup. The launcher
still reports the server ONLINE, because `Core.ServerAvailable` resolves it from
.NET quite happily, so the symptom is a client that silently never connects.
`ResolveToIPv4` fixes it where the value is written into `server.dat`. Numeric
addresses pass straight through, costing no lookup and producing a byte-identical
file; a failed lookup passes the original string through rather than blocking the
launch, so a literal-IP configuration that never needed DNS cannot be broken by
this. `ENABLE_HOSTNAME`/`HOSTNAME` in `CLHook.ini` does **not** cover this — that
path is in `CLHook.dll`, and clients from 6000 up launch with `COHook.dll` and the
`server.dat` mechanism, so for a 6300 client those keys are read by nobody.

**The CLServer connect used to stall the launch for ~21 seconds.**
`SimpleTcpClient.Connect` inherits the OS connect timeout, which is what Windows
waits when the SYN is dropped rather than refused — what a filtering firewall
does, an AWS security group for one. Both launch paths make that call between
starting `conquer.exe` and injecting `COHook.dll`, so the whole wait is spent
with the client running and unhooked: it reads the original encrypted
`Server.dat`, builds the stock server list and dials a long-dead TQ address,
never seeing the generated one. A server on localhost refuses at once, which is
why this only ever broke for remote hosts — and so looked like a server-side or
DNS problem. `EnsureReachable` probes with a 2s bound and throws the same
`SocketException` a failed connect would; reachable servers behave as before.

**CLServer itself is off.** It answers "does this IP have a live loader
connection?", so a game server can refuse players who launched `conquer.exe`
directly or with a bot. It cannot establish that: the socket carries no token and
is never correlated with the game session it vouches for, so any TCP connect to
port 8000 from the same address passes, one connection whitelists everyone behind
a NAT, and a changing address locks out a legitimate player. The connection list
also round-trips through `api.conquerloader.com` keyed by a license key that
CLServer ships hardcoded, so every operator running it stock shares one namespace
and overwrites the others. Nothing here consumes `CheckConnectionByIP`, so the
connect only ever cost a socket — and, without the bound above, most of the
launch.

## Building

```
msbuild ConquerLoader\ConquerLoader\ConquerLoader.csproj -t:Restore -p:RestorePackagesConfig=true -p:SolutionDir=<repo>\ConquerLoader\
msbuild ConquerLoader\ConquerLoader\ConquerLoader.csproj -t:Build -p:Configuration=Release -p:SolutionDir=<repo>\ConquerLoader\
dotnet test ConquerLoader\Tests\CLCore.Tests\CLCore.Tests.csproj
```

The hook that carries the frame cap is a separate, 32-bit, C++ build:

```
msbuild ConquerCipherHook\ConquerCipherHook.vcxproj -t:Restore;Build -p:RestorePackagesConfig=true -p:Configuration=Release -p:Platform=Win32 -p:SolutionDir=<repo>\ConquerLoader```

**It is not a build input for the loader.** The loader embeds
`ConquerLoader/Resources/ConquerCipherHook.dll` as a resource, so changing the
hook means building it and committing the new DLL over that one. CI builds the
vcxproj to prove the source still compiles, and nothing more - a byte-compare
against the committed DLL would only prove which toolset built it.

Output is `ConquerLoader/Release/ConquerLoader.exe`, ~47 MB — Costura.Fody packs
every dependency, and the two `VC_redist` installers upstream embeds, into the
one file.

**The retarget to v4.8 is why this builds at all on a normal machine.** The
4.6.2 targeting pack is not installed by Visual Studio 2022 by default and is
not on the GitHub Windows runner images, so upstream's `v4.6.2` fails with
MSB3644 until somebody finds and installs a developer pack. 4.8 ships in-box on
Windows 10 1903 and later, which is the whole population that can run this
client anyway.

`Tests/CLCore.Tests` is **not** in `ConquerLoader.sln`, on purpose: that solution
is built by MSBuild-for-.NET-Framework and carries two C++ projects, and the
test project is SDK-style and run by `dotnet test`.
