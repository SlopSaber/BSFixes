# BSFixes

Small Beat Saber 1.45.0 compatibility fixes.

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
per-application resolution settings every two seconds. If either changes while
the game runs, it resizes the eye textures. Beat Saber's own VR resolution
multiplier still applies in menu and gameplay. Switching SteamVR between
automatic and manual resolution requires a game restart because the OpenXR
runtime's base recommendation changes.
