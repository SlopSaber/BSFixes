# BSFixes

Small Beat Saber 1.45.0 compatibility fixes.

Current behavior filters only confirmed harmless Unity startup noise:

- stale Quest-only SettingsIO properties on PC;
- missing DLC promo assets;
- Camera.stereoTargetEye warning under a scriptable render pipeline;
- malformed internal cvar.System.* command-name errors.

LIV errors stay visible because they can indicate a real capture problem.
