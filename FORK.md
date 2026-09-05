# What this fork changes

Fork of [OpenConquerOrg/ConquerLoader](https://github.com/OpenConquerOrg/ConquerLoader).
It exists for one reason: **the loader updates the client before it starts it**,
so the patch step cannot be skipped.

Upstream is unchanged in every other respect. There is no rebranding, no UI
surgery and no removed features, deliberately — every change below is additive
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
| `CLCore/Models/ServerConfiguration.cs` | **One field**, `PatchManifestUrl`. Null or empty — which is what every existing `config.json` has — turns patching off and the loader behaves exactly as it always did |
| `ConquerLoader/Core.cs` | **One method**, `RunAutoPatch`, returning the same `PluginPreLaunchResult` the plugin hook already uses so both launch paths reuse the cancel-launch plumbing |
| `ConquerLoader/Forms/Main.cs`, `Forms/WPF/MainLite.xaml.cs` | **Six lines each**, calling it immediately before `RunPreLaunchPlugins` |
| `Tests/CLCore.Tests/` | **New.** 52 tests over the path guard, the manifest validation and the compare-and-install pair |
| every `*.csproj` | `TargetFrameworkVersion` v4.6.2 → **v4.8** |

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

## Building

```
msbuild ConquerLoader\ConquerLoader\ConquerLoader.csproj -t:Restore -p:RestorePackagesConfig=true -p:SolutionDir=<repo>\ConquerLoader\
msbuild ConquerLoader\ConquerLoader\ConquerLoader.csproj -t:Build -p:Configuration=Release -p:SolutionDir=<repo>\ConquerLoader\
dotnet test ConquerLoader\Tests\CLCore.Tests\CLCore.Tests.csproj
```

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
