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
