# Separate Player And Editor Transports

The Host presents one QA command surface but uses two target adapters: packaged Players keep the authenticated named-pipe BepInEx plugin, while Unity Editor uses an Editor-only embedded UPM package with authenticated loopback HTTP. The Editor package ships a dedicated runtime assembly that reuses the existing reflection core without loading BepInEx, Harmony, or the Player plugin, because Unity projects can already contain incompatible Harmony versions and scan every imported assembly during Play Mode startup.

## Consequences

Editor targets support read-only discovery in Edit Mode and QA runtime commands in Play Mode. Automatic play and Harmony-dependent map skipping remain Player-only; Editor credentials stay behind the Host boundary, and the package does not modify `Assets`, `Packages/manifest.json`, or Player builds.
