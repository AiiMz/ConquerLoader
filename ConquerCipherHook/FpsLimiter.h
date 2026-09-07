#pragma once

/**
* A real frame limiter for the client.
*
* The loader has carried an "FPS Unlock" toggle for years that reads and writes
* config.json and is then read by nothing at all - the client has no frame cap
* of its own to unlock, so it draws as fast as the machine allows. On a modern
* GPU that is 700+ FPS, and because the client advances its animations per
* frame rather than per unit of time, everything on screen runs at several
* times the speed it was drawn for.
*
* So the fix is a cap rather than a toggle. This installs one by replacing
* IDirect3DDevice8::Present in the device vtable and sleeping in front of the
* original until the frame is due.
*
* maxFps <= 0 caps nothing and the client draws exactly as it did before.
*
* debug writes a `CLHook.fps.log` next to conquer.exe, a block every ten
* seconds: how many times Present was actually called, how those calls were
* spaced, and which devices and windows they went to. It is worth having
* because the number the client draws on screen is its own count of rendered
* frames, which is not the same quantity as the one this paces, and when the
* two disagree only this can say by how much. It installs the hook even when
* maxFps is 0, so the uncapped client can be measured.
*
* Returns immediately in every case: the hook goes in from a worker thread,
* once d3d8.dll is in the process.
*/
void FpsLimiter_Install(int maxFps, bool debug);
