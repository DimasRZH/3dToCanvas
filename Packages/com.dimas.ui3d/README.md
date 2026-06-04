# UI3D — 3D Characters in UGUI

Render skinned 3D characters directly inside a Unity uGUI `Canvas` using
Burst-accelerated CPU skinning. A `UIBurstSkinnedCharacter` draws an animated,
skinned mesh as a regular UI graphic, with correct depth handling so the model
composites cleanly against other UI.

## Installation

This is an embedded UPM package. Copy the `com.dimas.ui3d` folder into your
project's `Packages/` directory, or add it to `Packages/manifest.json`:

```json
"com.dimas.ui3d": "file:../path/to/com.dimas.ui3d"
```

## Requirements

- Unity 6000.2+
- `com.unity.burst`, `com.unity.mathematics`, `com.unity.ugui` (declared as
  package dependencies and installed automatically)

## Contents

| Assembly      | Folder      | Purpose                                              |
|---------------|-------------|------------------------------------------------------|
| `UI3D`        | `Runtime/`  | Runtime components, skinning jobs, and shaders.      |
| `UI3D.Editor` | `Editor/`   | Editor tooling for baking/inspecting character data. |

### Runtime

- `UIBurstSkinnedCharacter` — the main component; place on a Canvas child.
- `UIBurstSkinnedRenderer` — the `MaskableGraphic` that submits the skinned mesh.
- `UIBurstSkinManager`, `UIBurstSkinningJob` — Burst skinning pipeline.
- `UIBurstCharacterData` — baked character/bone data (`ScriptableObject`).
- `UIBurstSortOverrideBehaviour` — animator state hook for draw-order control.
- `Shaders/` — `UI/3DModel` and `UI/DepthClear` shaders plus the default material.

> Note: `UIBurstSkinnedCharacter` falls back to `Shader.Find("UI/3DModel")` and
> `Shader.Find("UI/DepthClear")` if shader references are not assigned, so the
> shaders in `Runtime/Shaders/` must remain in the project.
