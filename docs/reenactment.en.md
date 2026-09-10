# Custom Reenactment (Stage Scene) Configuration Guide

[中文版](reenactment.zh-Hans.md)

> **Status: experimental.** Verified in game with a proof-of-concept mod; the JSON format may still change based on feedback.

For mod authors: how to build your own "reenactment" scenes, the curtain-framed stage sequences the vanilla game uses during trials to replay what happened (a character, some props, an arrow flying, and so on). You bring any pictures, place them where you like, and animate them with keyframes. No Unity knowledge needed.

---

## 0. Quick start

**Your reenactment = the vanilla curtain shell + your own layers + your own keyframe animation.** The black letterbox, the theatre curtain and its opening / closing animation come from a vanilla scene; everything on the stage is yours.

### Step 1: prepare the pictures

Put PNG files (transparent background where needed) in any sub-folder of your mod folder, for example `Reenact/`.

- Stage coordinates are in **world units**. How many pixels make one unit is set per layer with `PixelsPerUnit` (default 100).
- The camera shows about **27 × 15 units**; opaque side bars hide everything beyond roughly ±12 units horizontally, so the usable stage is about **24 × 15 units**. Centre of the stage is `(0, 0)`, x grows to the right, y grows upward.
- Vanilla setup, worth copying: a 2048×1024 background with `PixelsPerUnit` 50 at scale 0.7 spans 28.7 × 14.3 units and fills the stage; characters and props (a few hundred pixels) use `PixelsPerUnit` 100 at scale 0.7.
- A picture's pivot defaults to its centre; position, scale and rotation are all applied around the pivot.

### Step 2: add `Reenactments` at the top level of `info.json`

```json
"Reenactments": [
  {
    "Id": "MyMod_Scene1",
    "Template": "Reenact_Ch01_E01_S02",
    "Layers": [
      { "Name": "bg",    "Sprite": "Reenact/bg.png",    "Position": [0, 0],        "Scale": 0.7, "Order": 0, "PixelsPerUnit": 50 },
      { "Name": "hero",  "Sprite": "Reenact/hero.png",  "Position": [0, -0.5],     "Scale": 0.7, "Order": 10 },
      { "Name": "arrow", "Sprite": "Reenact/arrow.png", "Position": [-2.4, -1.5],  "Scale": 0.7, "Order": 20, "Visible": false }
    ],
    "Steps": [
      {
        "Duration": 3.0,
        "Tracks": [
          { "Layer": "arrow", "Keys": [
            { "Time": 0.5, "Visible": true },
            { "Time": 2.0, "Position": [2.1, -0.7], "Ease": "OutQuad" }
          ] }
        ]
      },
      { "Duration": 1.5, "Tracks": [
        { "Layer": "arrow", "Keys": [ { "Time": 0, "Alpha": 1 }, { "Time": 1.5, "Alpha": 0 } ] }
      ] }
    ]
  }
]
```

### Step 3: call it from the script

```nani
@set "isReenact = true"                 ; vanilla flag: BeginAdv hides the witch-book button while a reenactment runs
@printer Normal.Simple time:0
@gosub System/System_Subroutine.BeginAdv

@spawn "MyMod_Scene1" params:0 !wait     ; curtain opens and step 0 starts; the script does not wait
@wait 1.5                                ; covers the curtain opening (about 1.2 s)
Hero: So this is how it happened.        ; normal dialogue over the open stage, waits for the click as usual
Hero: And then it was over.
@resetText                               ; empty the text box before the curtain closes
@despawn "MyMod_Scene1" !wait            ; curtain closes (2 s), stage removed
@wait 1.5

@set "isReenact = false"
```

This is exactly how the vanilla trial scripts are written (taken from the game's own Act01_Chapter01_Trial15): spawn and despawn are fire-and-forget (`!wait`), fixed `@wait` calls cover the curtain animation, dialogue prints over the open stage, and `@resetText` clears the text box before the curtain closes. The vanilla scripts even put an `@autoSave` right after the spawn. `params:N` selects the step (0-based); to play another step on the same open stage, call `@spawn` again with a different `params:` and no `@despawn` in between. If you need the script to block until a step's animation has finished, write `wait:true` instead of `!wait`.

### Step 4: test in game

Open `BepInEx/LogOutput.log` and search for `[ReenactLoader]`:

- `Registered mod reenactment 'MyMod_Scene1'` — the scene is ready.
- `SetSpawnParameters on 'MyMod_Scene1' ... armed` — the script reached it.
- Warnings name a missing picture, a misspelled layer, a bad colour, a step index out of range, or a template that never loaded.

---

## 1. `Reenactments[]` fields

| Field | Required | Meaning |
| --- | --- | --- |
| `Id` | yes | Spawn path used by `@spawn` / `@despawn`. Must be unique across all mods; prefix it with your mod name. |
| `Template` | no | Vanilla scene whose shell (letterbox, curtain, open / close animation) is reused. Default `Reenact_Ch01_E01_S02`. See §4. |
| `Layers` | yes | The pictures on the stage (§2). |
| `Steps` | no | The animation steps (§3). If omitted, one 1-second step with no animation. |

## 2. `Layers[]` fields

| Field | Default | Meaning |
| --- | --- | --- |
| `Name` | — | Unique name inside this reenactment; tracks refer to it. |
| `Sprite` | — | Picture path relative to the mod folder, `/` as separator. |
| `Position` | `[0, 0]` | `[x, y]` in world units. |
| `Scale` | `1` | A number (uniform) or `[x, y]`. Vanilla art is drawn for 0.7. |
| `Rotation` | `0` | Degrees, counter-clockwise. |
| `Pivot` | `[0.5, 0.5]` | Pivot inside the picture, 0–1 from bottom-left. |
| `Alpha` | `1` | Opacity 0–1. |
| `Tint` | white | HTML colour multiplied into the picture. |
| `Order` | `0` | Stacking order; larger draws on top. Layers with equal order draw in declaration order. |
| `Visible` | `true` | Initial visibility. |
| `PixelsPerUnit` | `100` | Pixels per world unit for this picture. Use 50 for full-stage 2048×1024 backgrounds, 100 for characters and props (vanilla values). |

Layers are created in declaration order and keep their state between steps.

## 3. `Steps[]`, `Tracks[]` and `Keys[]`

- `Steps[i].Duration` — seconds. The step's keyframes play over this time. With `!wait` (vanilla style) the script continues immediately and the animation runs under the dialogue; with `wait:true` the script blocks until the curtain opening plus this duration has elapsed.
- `Steps[i].Tracks[]` — one entry per animated layer: `{ "Layer": "<name>", "Keys": [ ... ] }`. Layers without a track keep their current state.
- Each key has a `Time` (seconds from the start of the step) and any of the properties below. Only write what changes.

| Key property | Interpolated | Notes |
| --- | --- | --- |
| `Position` `[x, y]` | yes | |
| `Scale` number or `[x, y]` | yes | |
| `Rotation` | yes | degrees |
| `Alpha` | yes | 0–1 |
| `Tint` | yes | HTML colour |
| `Visible` | no | switches at the key's time |
| `Sprite` | no | swaps the picture at the key's time (path relative to the mod folder) |
| `Ease` | — | how the value approaches **this** key from the previous key that defines the same property |

Interpolation rules: every property is interpolated between the two neighbouring keys that define it. Before the first key that defines a property the value is held at that key; after the last one it stays there. So `{ "Time": 0.5, "Visible": true }` alone makes a layer appear at 0.5 s without touching its position.

`Ease` values: `Linear` (default), `InQuad`, `OutQuad`, `InOutQuad`, `InCubic`, `OutCubic`, `InOutCubic`, `InSine`, `OutSine`, `InOutSine`, `Step` (hold the previous value, then jump at the key's time).

## 4. Templates

Any vanilla reenactment can serve as the shell. All 56 share the same curtain and letterbox; the stage contents are discarded, so the choice only matters if the shells differ in a way you care about. Names follow `Reenact_Ch<act>_E<chapter>_S<scene>`, for example:

`Reenact_Ch01_E01_S01` … `S06`, `Reenact_Ch01_E02_S01` … `S05`, `Reenact_Ch01_E03_S01` … `S06`, `Reenact_Ch01_E04_S01` … `S04`, `Reenact_Ch01_E05_S01` … `S08`, `Reenact_Ch02_E01_S01` … `S05`, `Reenact_Ch02_E02_S01` … `S06`, `Reenact_Ch02_E03_S01` … `S06`, `Reenact_Ch02_E04_S01` … `S05`, `Reenact_Ch02_E06_S01` … `S05`.

With `Debug.OpenDebug = true` in `BepInEx/config/ManosabaLoader.cfg`, the loader prints the full hierarchy of each template it loads (nodes, sprites, sorting, directors) to the log.

## 5. Getting the vanilla layer art

The vanilla stage layers are plain PNGs inside the game data (for scene 1-1-2: `1-1-2_Leia.png`, `1-1-2_Bow.png`, `1-1-2_Arrow.png`, `Background_1-1-2.png`). Export them with AssetRipper as described in the cut-in guide, §2. They are useful as size and position references: in the vanilla 1-1-2 scene (positions read from the running game) the background sits at `(0, 0)`, Leia at `(-0.01, -0.48)`, the bow at `(2.42, -1.55)` and the arrow at `(-2.08, -0.71)`, all at scale 0.7, with `PixelsPerUnit` 50 for the background and 100 for the others. Vanilla stage layers use sorting orders between -120 and 0 and sit under a semi-transparent dark overlay; your `Order` values are added to the vanilla base order, so keep them between 0 and 100. The `*_kari` pictures are development placeholders (仮) and are not shown in game.

## 6. Behaviour notes and limits

- Saving and loading restores the stage through the game's own spawn state; on restore the current step is shown at its final pose.
- Skipping fast-forwards the stage to the final pose of the step.
- The scene is a spawned object: `@despawn` (curtain closing) is required before the next scene change, exactly like the vanilla scripts.
- The shell's curtain, letterbox and their animations cannot be changed.
- Skeletal (bone) animation is not supported; split a character into parts and animate the parts.
- Vanilla reference: each vanilla scene is one spawn with a single story timeline; multiple `Steps` on one stage are this loader's addition. Everything else (flags, waits, `@resetText`) is shown in Step 3.
- About `isReenact`: the only place the game reads it is the `BeginAdv` subroutine, which shows the witch-book button only when `hasWitchBook && !isReenact`. Setting it to `true` around your sequence keeps that button hidden while the stage is up, exactly as in the vanilla trials; if your mod never grants the witch book it changes nothing, but keeping it costs nothing and stays compatible with future game logic.
