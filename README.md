# BSFixes

Small Beat Saber 1.45.2 compatibility fixes.

High obstacle quality now remains available with Screen Distortion Effects off.
BSFixes keeps the high-quality wall shader and its screen/depth capture, but sets
wall refraction strength to zero on owned material copies. The graphics checkbox
and saved setting stay off. Turning distortion on restores normal refraction;
Low and Medium obstacle quality retain their existing material selection.
The required screen capture still has a rendering cost. This change does not
establish that legacy custom-map water renders correctly after the URP transition.

Current behavior filters only confirmed harmless Unity startup noise:

- stale Quest-only SettingsIO properties on PC;
- missing DLC promo assets;
- deprecated OnLevelWasLoaded notices from the loader;
- Camera.stereoTargetEye warning under a scriptable render pipeline;
- malformed internal cvar.System.* command-name errors.

When no LIV app path exists in `LIV_APP_PATH` or the LIV app registry key,
BSFixes skips Beat Saber's LIV initialization. An installed LIV app keeps the
game's normal integration and error reporting.

On SteamVR/OpenXR, BSFixes checks SteamVR's global manual and Beat Saber
per-application resolution settings twice per second. If either changes while
the game runs, it updates the active OpenXR display's render target scale.
BSFixes logs the render-pass width after each change. Beat Saber's own VR resolution
multiplier still applies in menu and gameplay. Switching SteamVR between
automatic and manual resolution also triggers an eye-texture resize. SteamVR
does not save its effective automatic scale, so a restart gives the exact
runtime-recommended size if automatic scaling differs from 100%.
