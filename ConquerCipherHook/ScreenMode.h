#pragma once

/**
* Fullscreen at a size the client cannot do itself.
*
* The client's own fullscreen only supports 800x600 and 1024x768. Asking it for
* fullscreen at a custom size, or pressing its in-game fullscreen button, gives
* 1024x768. So for a custom size the loader starts the client windowed at that
* size (ScreenModeRecord 4) and writes FULLSCREEN=1 with SCREEN_WIDTH and
* SCREEN_HEIGHT into CLHook.ini. This then:
*
*   1. switches the primary display to that mode (CDS_FULLSCREEN, a temporary
*      mode Windows drops again when the client exits), before the client has
*      created its window or its Direct3D device, so the device is never lost;
*   2. waits for the client's window and removes its frame, putting its client
*      area exactly over the screen.
*
* The client still believes it is windowed, so its back buffer, mouse
* coordinates and device handling are unchanged. Nothing in Direct3D is hooked.
*
* If the display cannot take that mode and is not already at it, the window is
* left as an ordinary window rather than a frameless one in a corner.
*
* What happened is written to CLHook.screen.log next to conquer.exe.
*
* Returns immediately: the work is done on a worker thread.
*/
void ScreenMode_Install(bool fullscreen, int width, int height);
