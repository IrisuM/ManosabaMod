# Custom Cut-in (Objection Scene) Configuration Guide

[中文版](cutin.zh-Hans.md)

For mod authors: which pictures to draw, what to write in `info.json`, and what each field changes on screen. No Unity knowledge needed.

---

## 0. Quick start (read this first)

**Your cut-in = the vanilla Hiro objection animation + your own pictures.** Timing, camera and sound effects stay as in the original; you only supply images and tell `info.json` which vanilla picture is replaced by which of yours. Layer tints, background color, glass effects and sparkles can be changed too, and any layer can be switched off entirely (§3.5), but all of that is optional.

### Step 1: prepare the pictures

Put them in any sub-folder of your mod folder (the folder that contains `info.json`), for example `Cutins/` (the folder name is up to you). Requirements:

- **PNG** with a transparent background (JPG has no transparency and would add a solid rectangle).
- **Same canvas size as the vanilla picture**, with the character in the same place. Easiest way: put the vanilla picture (§2 shows how to export it) on a bottom layer, draw over it, delete the bottom layer.

| What to draw | Size | Notes |
| --- | --- | --- |
| Character ×3 | 1556×2048 | Three expressions. **All three must have exactly the same outline** (only the face differs), see §3.3. One drawing is fine too: use the same file for all three keys. |
| Character shadow | 1625×2048 | Solid white silhouette of the character (the displayed color is set in the config, default blue-grey). |
| Character glow | 1603×2048 | White silhouette with soft blurred edges. |

If you replace the character but not the shadow / glow, the vanilla Hiro silhouettes will show from behind your character.

### Step 2: add `CutIns` at the top level of `info.json`

Next to your existing sections such as `Characters` or `Clues`:

```json
"CutIns": [
  {
    "Id": "MyMod_MyChar",
    "Sprites": {
      "Hiro_CutIn_001": "Cutins/MyChar_001.png",
      "Hiro_CutIn_002": "Cutins/MyChar_002.png",
      "Hiro_CutIn_003": "Cutins/MyChar_003.png",
      "Hiro_CutIn_ShadowWhite": "Cutins/MyChar_Shadow.png",
      "Hiro_CutIn_luminescence": "Cutins/MyChar_Glow.png"
    }
  }
]
```

- `Id`: must be unique across all mods; prefix it with your mod name.
- `Sprites`: left of the colon is the vanilla picture name (full list in §3.1, case-sensitive), right is the path to your picture (relative to the mod folder, use `/`).
### Step 3: call it from the script

```nani
@gosubCutIn "MyMod_MyChar" index:1
```

`index` selects which character picture is shown, and **the numbers do not match**:

| Script says | Shows the picture you put under |
| --- | --- |
| `index:1` | `Hiro_CutIn_002` |
| `index:2` | `Hiro_CutIn_003` |
| `index:3` | `Hiro_CutIn_001` |

The script continues automatically after the animation. Other parameters: §7.

### Step 4: test in game

If a picture does not show, open `BepInEx/LogOutput.log` in the game folder and search for `[CutInLoader]`: a missing file, an image that could not be decoded, or a misspelled `Sprites` name each produce one warning line naming the culprit.

### All fields of a `CutIns` entry at a glance

| Field | What it does | Details |
| --- | --- | --- |
| `Id` | The name you call from the script. Required. | §0 |
| `Sprites` | Which vanilla pictures are replaced by which of yours. | §3 |
| `Tints` | A color multiplied over one picture. | §3.4 |
| `HiddenLayers` | Parts of the scene to switch off entirely. | §3.5 |
| `MeshAlphaThreshold` | Fixes grey edges around a custom glass band. Normally leave it out. | §3.6 |
| `Shaders` | Background color, glass effects, shadow and glow colors. | §4 |
| `Particles` | The sparkles. | §5 |
| `BaseTemplate` | Which vanilla cut-in provides the animation. Only `"Hiro"` exists, so leave it out. | §10.2 |

---

## 1. Glossary

| Term | Meaning |
| --- | --- |
| vanilla | The unmodded game. |
| layer / sprite | The cut-in is dozens of pictures stacked on top of each other; each is a layer, and its sprite is "the picture that layer uses". |
| key | The vanilla picture name on the left side of a `Sprites` entry. |
| group | A named set of layers that can be switched off together, see §3.5. |
| opacity / alpha | How see-through a pixel is: 0 = fully transparent, 255 (or 100 %) = solid. |
| material / shader | A group of settings that decides how a layer is colored and animated. You only ever touch their config fields, never the things themselves. |
| mask | A white-on-black image. White = effect applies, black = no effect. |
| particles | The small sparkles flying across the screen. |
| dump | A listing the loader writes to the log in debug mode, see §8. |

---

## 2. Getting the vanilla pictures as templates

The vanilla pictures live inside the game's data files. AssetRipper, a free tool, pulls them out as PNGs.

### 2.1 Export with AssetRipper

1. Download AssetRipper from its GitHub releases page (search "AssetRipper github"; take the Windows x64 zip) and unzip it anywhere.
2. Run `AssetRipper.GUI.Free.exe`. It opens a page in your browser.
3. On that page choose **File → Open Folder** and select the game's install folder (in Steam: right-click the game → Manage → Browse local files).
4. When loading has finished, choose **Export → Export Primary Content** and pick an empty output folder.
5. The pictures are in `Assets/Texture2D/` inside that output folder, named exactly like the keys in §3.1 (e.g. `Hiro_CutIn_001.png`). The glass masks and the particle texture are there too.

### 2.2 Drawing rules

- Transparent-background PNG for everything.
- Same canvas size as vanilla, character in the same place. The shadow canvas (1625 wide) and glow canvas (1603 wide) are wider than the character canvas (1556 wide); align each against its own vanilla picture before drawing.
- The three expression pictures must share an identical outline, otherwise ghosting appears when the expression changes (§3.3).
- Shadow and glow are plain white silhouettes; their colors come from `Shaders.CharacterShadow.Color` / `Shaders.CharacterGlow.Color` (§4.4, §4.5).
- A different size is not scaled to fit: a bigger picture appears bigger on screen (and may overflow), a smaller one appears smaller.

---

## 3. `Sprites` — replacing pictures

### 3.1 Replaceable layers (all keys)

| Key (vanilla picture name) | Size | What it is on screen | What to draw / notes |
| --- | --- | --- | --- |
| `Hiro_CutIn_001`<br>`Hiro_CutIn_002`<br>`Hiro_CutIn_003` | 1556×2048 | Character art (three expressions) | Full-color, transparent background, identical outline. See §3.3. |
| `Hiro_CutIn_ShadowWhite` | 1625×2048 | Character shadow | Solid white silhouette. Color from `Shaders.CharacterShadow.Color`. |
| `Hiro_CutIn_luminescence` | 1603×2048 | Character glow | Soft-edged white silhouette. Color from `Shaders.CharacterGlow.Color`. |
| `Hiro_CutIn_StainedGlass_001`<br>`Hiro_CutIn_StainedGlass_002`<br>`Hiro_CutIn_StainedGlass_003` | 2048×883 | The stained-glass band across the screen (three slightly different versions shown one after another) | Full-color, transparent background. **Drawing rule:** the glass does not support semi-transparency. Every pixel that is at least about 25 % opaque is shown as solid glass, anything fainter is not shown at all. So do not feather the edges, and keep the empty area free of faint leftover pixels, or you get hard edges and grey specks (the 25 % is adjustable, §3.6). What lies under fully transparent pixels does not matter. Flame, cracks and highlights on the glass are added separately by `Shaders.StainedGlass` (§4.2). |
| `Hiro_CutIn_StainedGlass_luminescence001` | 2048×883 | Glow / dissolve layer of the glass band | White silhouette of the band. **Note: no underscore before `001` in this name.** Color from `Shaders.StainedGlassGlow` (§4.3). |
| `RefuteCutIn_StainedGlassGlow_001`<br>`RefuteCutIn_StainedGlassGlow_002`<br>`RefuteCutIn_StainedGlassGlow_003` | 2048×1032 | Pink soft glow behind the glass band | Pale pink soft-edged silhouette. Despite the name, this is **not** the same layer as the row above or as `Shaders.StainedGlassGlow`. |
| `Hiro_GlassFragment_001`<br>`Hiro_GlassFragment_002`<br>`Hiro_GlassFragment_003` | 2048×1017 | Flying glass fragments (three groups, moved by the animation) | Fragment art on transparent background. |
| `White` | 32×32 | Full-screen white flash | Normally leave alone. To remove the flash, add `White` to `HiddenLayers` (§3.5). |

The prefab also contains a few leftover development reference screenshots (`Background2`, `Hiro_Background000`, `CutIN_Ema`, `CutIN_Ema2`). They are not visible in game; ignore them.

### 3.2 Rules

- The value is a picture path relative to the mod folder (sub-folders allowed, use `/`), or `"none"`.
- `"none"` = replace the layer with a fully transparent picture. It works, but `HiddenLayers` (§3.5) is the better way to switch a layer off.
- Keys you do not write keep the vanilla picture.
- Missing / undecodable file → a warning in the log, the layer keeps vanilla.
- Misspelled key (including wrong case) → a warning `these Sprites keys match no layer`, the key is ignored.
- Every replacement picture is cut along its own outline; how transparent counts as "outside" is set by `MeshAlphaThreshold`, see §3.6.

### 3.3 `index` and the three character pictures

`index:1 / 2 / 3` shows `Hiro_CutIn_002 / 003 / 001` (table in §0, step 3).

When different `index` values are used within one trial, **previously shown character pictures are not switched off**, so several pictures end up stacked. The three vanilla pictures have identical outlines, so nothing shows; if you replace only one, a vanilla Hiro shows through from behind. So fill all three, one of:

- three expressions with exactly the same outline;
- the same file for all three;
- `"none"` for the ones you do not use (or list them in `HiddenLayers`, §3.5), and do not call the matching `index` in the script.

### 3.4 `Tints` — changing a layer's color multiplier

Besides its picture, every layer has a "tint": a color multiplied into the picture. Almost all vanilla layers are white (picture unchanged); only the glass glow layer `Hiro_CutIn_StainedGlass_luminescence001` is pale pink `#FFDEFF`. Keys are the same as in `Sprites`, values are HTML colors:

```json
"Tints": {
  "Hiro_CutIn_StainedGlass_luminescence001": "#FFFFFF",
  "Hiro_CutIn_001": "#C0C0FF"
}
```

- Tinting multiplies: white = unchanged, `#FF8080` = redder with the other channels darkened, a color with `AA` makes the whole layer translucent.
- Layers not listed keep their vanilla tint; it is restored automatically when a vanilla cut-in plays.
- Some layers have their tint driven by the scene animation (e.g. the transparency of the `White` flash); the channels the animation writes override your value.

### 3.5 `HiddenLayers` — switching parts of the scene off

Anything in the cut-in can be switched off completely: a single picture, a whole group of pictures, the sparkles, or the full-screen background. Write a list of names:

```json
"HiddenLayers": ["BackGround", "Glass2", "White"]
```

**Where the names come from.** The pictures of the cut-in are organised in named groups, much like files in folders. A name in this list is either a picture name from §3.1, or a group name; a group name switches off every picture inside that group. Upper / lower case does not matter (unlike `Sprites`). These are all the groups, indented to show what contains what:

```
BackGround   full-screen background (red-and-black noise gradient)
CutIN        the whole glass band with its lights
  Glow         pink soft glow behind the band (RefuteCutIn_StainedGlassGlow_001..003)
  CutIN        the three glass band pictures + the glow / dissolve layer Hiro_CutIn_StainedGlass_luminescence001
Hiro         the three character pictures + shadow + character glow
Glass2       the three groups of flying fragments
Glass        the sparkles (the game's own name for them; nothing to do with the glass band)
White        the white flash
```

The same names appear in the debug log (§8, step 4). Common choices:

| What to switch off | Write |
| --- | --- |
| Full-screen background (red-and-black noise gradient) | `BackGround` |
| Character, shadow and character glow together | `Hiro` (a group name; your own character is in this group too, whether or not you replaced the pictures) |
| Character pictures only (keep shadow and glow) | `Hiro_CutIn_001`, `Hiro_CutIn_002`, `Hiro_CutIn_003` |
| Character shadow only | `Hiro_CutIn_ShadowWhite` |
| Character glow only | `Hiro_CutIn_luminescence` |
| The whole glass band, with its glow / dissolve layer and the pink glow behind it | `CutIN` |
| Glass band pictures only (keep both glows) | `Hiro_CutIn_StainedGlass_001`, `Hiro_CutIn_StainedGlass_002`, `Hiro_CutIn_StainedGlass_003` |
| Glow / dissolve layer of the glass band only | `Hiro_CutIn_StainedGlass_luminescence001` |
| Pink soft glow behind the glass band only | `Glow` |
| Flying glass fragments | `Glass2` |
| Sparkles | `Glass` |
| White flash | `White` |

**Careful with three look-alike names:** `Glass` is the sparkles, `Glass2` is the flying fragments, and the stained-glass band itself is `CutIN`.

What happens on screen:

- A switched-off part is simply not drawn. Its `Sprites` / `Tints` / `Shaders` settings are still applied, you just do not see them.
- It stays off for the whole cut-in, even though the animation normally turns some parts on and off during the scene.
- With the background off, whatever the game was showing before the cut-in (normally the courtroom) stays visible behind the character and the glass band. There is no picture layer for a background of your own: the background can only be recolored (§4.1) or switched off.
- Parts you do not list keep their vanilla visibility, and everything is put back automatically when a vanilla cut-in plays.
- A name that matches nothing gives a warning `these HiddenLayers entries match no layer or node` in the log and is ignored.

Three ways to remove something, compared:

| I want to remove... | Write | Notes |
| --- | --- | --- |
| Any picture, a whole group, or the background | `"HiddenLayers": [ ... ]` | Preferred: nothing is drawn and no extra picture is loaded. |
| One picture | `"Sprites": { "<key>": "none" }` | Looks the same, but loads a blank picture, and does nothing for the background. |
| The cracks or shard highlights on the glass | `"Shaders": { "StainedGlass": { "CrackTexture": "none" } }` etc. | The only way. These are effects inside the glass, not separate pictures (§4.2). |
| The sparkles | `"HiddenLayers": ["Glass"]` | Same mechanism as the first row; `Glass` is the node the sparkle emitters sit under. |

### 3.6 `MeshAlphaThreshold` — fixing grey edges around the glass band

You only need this if your own glass band picture shows a grey outline or grey specks around it. Otherwise leave it out.

Why it happens: the glass band ignores the transparency of its picture. Everything inside the picture's outline is drawn as solid glass, however faint the pixel is. The game works out that outline on its own, but it leaves a margin of several pixels around the drawing and rounds off small notches, and in that margin the "transparent" pixels get drawn too. The vanilla pictures are prepared so that this is invisible; with a custom picture it shows up as a grey edge or grey specks.

What the loader does about it: it traces the outline itself, pixel by pixel, from the transparency of your PNG. `MeshAlphaThreshold` is the cut-off it uses: pixels at least this opaque count as inside the outline and are drawn; anything fainter is outside and is not drawn at all. 0 to 255, default 64 (about 25 %).

```json
"MeshAlphaThreshold": 64
```

What it means for your drawings:

- Glass band: give it crisp edges and a completely clean transparent area. A feathered edge turns into a hard edge at the cut-off, and faint leftovers in the "empty" area turn into grey specks. If specks still appear, raise the value (for example 128); if thin spikes of your drawing get cut off, lower it (for example 32).
- All other replaced pictures get the same cut, but they do respect transparency, so the only visible difference is that pixels fainter than the cut-off disappear. On a very soft glow you might notice the faintest outer fringe missing; lower the value if that bothers you.
- One value per cut-in, applied to every replaced picture in it.

---

## 4. `Shaders` — colors and effects

Write only the fields you want to change; everything else stays vanilla. The whole section is optional.

- Colors: `"#RRGGBB"` or `"#RRGGBBAA"` (omit `AA` = opaque; the `#` may be omitted).
- Numbers: decimals.
- Texture fields: a picture path relative to the mod folder, or `"none"`.
- Fields marked "advanced" can normally be left alone.
- "Vanilla" values were read from the Hiro material with the §8 procedure. Fields marked "animated" change by themselves during the scene; the table shows the resting value, and overriding them freezes the animation.
- "Internal name" is only needed to read the §8 dump.

### 4.1 `Background` — full-screen background

Vanilla is a red-and-black noise gradient.

| Field | Effect | Vanilla | Internal name |
| --- | --- | --- | --- |
| `PrimaryColor` | Main background color | `#FF3C45` | `_BackgroundA` |
| `SecondaryColor` | Secondary color, mixed into the primary by noise | `#000000` | `_BackgroundB` |
| `BlendFactor` | Mix / noise strength. Advanced. | 0.3 | `_Float` |

```json
"Shaders": { "Background": { "PrimaryColor": "#1E90FF", "SecondaryColor": "#001030" } }
```

### 4.2 `StainedGlass` — effects on the glass band

The glass picture itself is replaced through `Sprites` (§3.1); this section changes the flame sweep and the masks drawn on top of it.

| Field | Effect | Vanilla | Internal name |
| --- | --- | --- | --- |
| `FlameColor` | Color of the flame / edge light sweeping across the glass | `#FF000B` | `_EclipseFlame` |
| `CrackTexture` | Thin crack lines on the glass. `"none"` removes them. | vanilla picture `Hiro_CutIn_StainedGlass_003_kirakira2` (white lines on black, 2048×883) | `_kirakira` |
| `ShardTexture` | Highlights on the shard blocks. `"none"` removes them. | vanilla picture `Hiro_CutIn_StainedGlass_003_kirakira1` (white blocks on black, 2048×883) | `_kirakira2` |
| `GlowMaskTexture` | Has no visible effect (the game never reads this slot); kept only for completeness, ignore it. | vanilla picture `RefuteCutIn_StainedGlass_luminescence001` (white band silhouette on black, 2048×883) | `_luminescence` |
| `Fader` | Sweep threshold 1 (0–1). Advanced, animated. | 1 | `_Fader` |
| `Fader2` | Sweep threshold 2 (0–1). Advanced, animated. | 1 | `_Fader2` |
| `Speed` | Sweep speed, higher = faster. Advanced. | 3 | `_Speed` |
| `Tick` | Sweep animation phase (0–1). Advanced, animated. | 1 | `_Tick` |
| `EdgeSize` | Edge light width, higher = wider. Advanced. | 0.02 | `_EdgeSize` |

Custom masks must be white-on-black images with the same size as the glass picture (2048×883).

```json
"Shaders": { "StainedGlass": { "CrackTexture": "none", "ShardTexture": "none", "FlameColor": "#66FFCC" } }
```

### 4.3 `StainedGlassGlow` — glow layer of the glass band

Applies to the `Hiro_CutIn_StainedGlass_luminescence001` layer.

| Field | Effect | Vanilla | Internal name |
| --- | --- | --- | --- |
| `Color` | Glow color. The result is your color × the layer's tint (vanilla pale pink `#FFDEFF`; change it to white with `Tints`, §3.4). | `#FDA5A4` | `_Color` |
| `FlameColor` | Flame color at the dissolve edge | `#FF000B` | `_EclipseFlame` |
| `Tick` | Dissolve animation phase (0–1). Advanced, animated. | 0.952 | `_Tick` |

### 4.4 `CharacterShadow` — character shadow

Applies to the `Hiro_CutIn_ShadowWhite` layer.

| Field | Effect | Vanilla | Internal name |
| --- | --- | --- | --- |
| `Color` | Shadow color | `#737D99` (blue-grey) | `_Color` |

### 4.5 `CharacterGlow` — character glow

Applies to the `Hiro_CutIn_luminescence` layer.

| Field | Effect | Vanilla | Internal name |
| --- | --- | --- | --- |
| `Color` | Glow color | `#FDA5A4` | `_Color` |
| `Tick` | Animation phase (0–1). Advanced. | 0.119 | `_Tick` |

---

## 5. `Particles` — sparkles

The small star-shaped sparkles flying across the screen. Only their look changes; count, speed and lifetime stay.

Change texture / color:

```json
"Particles": { "Texture": "Cutins/my_sparkle.png", "Color": "#AAFFFF" }
```

To switch the sparkles off entirely, do not use `Particles`; write `"HiddenLayers": ["Glass"]` (§3.5). `"Texture": "none"` looks the same but costs more.

| Field | Effect | Vanilla | Internal name |
| --- | --- | --- | --- |
| `Texture` | Picture of each sparkle | vanilla picture `kirakira` (128×128 white four-point star, transparent background) | `_BaseMap` |
| `Color` | Color multiplier, multiplied with the particles' own color | white | `_BaseColor` |

---

## 6. Where `"none"` works

Every place that accepts `"none"` (any case) gets a fully transparent picture:

- Any value in `Sprites` → hides that layer (`HiddenLayers`, §3.5, is the better way).
- `CrackTexture`, `ShardTexture` in `Shaders.StainedGlass` → removes that effect.
- `Particles.Texture` → invisible sparkles (`"HiddenLayers": ["Glass"]` is the better way).

Color and number fields do not accept `"none"`; leave them out instead.

The comparison table at the end of §3.5 shows when to use `HiddenLayers` and when `"none"`.

---

## 7. Script usage (all parameters)

```nani
@gosubCutIn "MyMod_MyChar" index:1
@gosubCutIn "MyMod_MyChar" index:2 voice:"MyMod_Voice/objection_01" volume:1
```

| Parameter | Required | Description |
| --- | --- | --- |
| (nameless) | yes | The cut-in's `Id`. |
| `index` | yes | 1 / 2 / 3, which character picture to show; mapping in §3.3. |
| `voice` | no | Voice resource path, written like any other voice in your mod. |
| `volume` | no | Voice volume, default 1. |
| `group` | no | Audio mixer group. Advanced, normally omitted. |
| `authorId` | no | Speaking character ID. Advanced, normally omitted. |

The script continues automatically after the animation.

---

## 8. Debug mode: reading the exact vanilla values

1. Open `BepInEx/config/ManosabaLoader.cfg` in the game folder (created after the game has been started once). In `[Debug]`, change `OpenDebug = false` to `OpenDebug = true`.
2. Your mod needs at least one cut-in entry (just an `Id` is enough) and a script line that calls it. Start the game and play up to that line.
3. Open `BepInEx/LogOutput.log` in the game folder and search for `First mod cut-in spawn`.
4. One line per layer, looking like this:
   `SpriteRenderer  Hiro/RefuteCutIn_Hiro_001 -> vanilla=Hiro_CutIn_001 now=ModCutIn_MyMod_MyChar_Hiro_CutIn_001 color=#FFFFFFFF active=True enabled=True sorting=Default:300 ...`
   - The path at the start, `Hiro/RefuteCutIn_Hiro_001`, is where the layer sits. Every part between the slashes can be written in `HiddenLayers`; here that is the group `Hiro` and the layer `RefuteCutIn_Hiro_001`.
   - `vanilla=` is the key to write in `Sprites` and `Tints`.
   - `color=` is the layer's current tint.
   - `enabled=False` means the layer is switched off by `HiddenLayers`.
   - `active=` is whether the animation currently has the layer on; just for information.
5. Below that, one line per material, e.g.
   `Background_Hiro (Shader Graphs/Background_0Fix): _Float=0.3 [Range], _BackgroundA=#FF3C4500 [Color], _BackgroundB=#00000000 [Color], ...`
   Colors are `#RRGGBBAA` and can be pasted into JSON as they are (the last two digits `AA` are transparency, which these effects ignore, so you may drop them); translate internal names such as `_BackgroundA` back to fields such as `PrimaryColor` with the "Internal name" column of the §4 tables.
6. It prints again only after going back to the title screen and into a trial. Set `OpenDebug` back to `false` when done.

---

## 9. Full example

```json
"CutIns": [
  {
    "Id": "MyMod_MyChar",
    "Sprites": {
      "Hiro_CutIn_001": "Cutins/MyChar_001.png",
      "Hiro_CutIn_002": "Cutins/MyChar_002.png",
      "Hiro_CutIn_003": "Cutins/MyChar_003.png",
      "Hiro_CutIn_ShadowWhite": "Cutins/MyChar_Shadow.png",
      "Hiro_CutIn_luminescence": "Cutins/MyChar_Glow.png",
      "Hiro_CutIn_StainedGlass_001": "Cutins/MyGlass_001.png",
      "Hiro_CutIn_StainedGlass_002": "Cutins/MyGlass_002.png",
      "Hiro_CutIn_StainedGlass_003": "Cutins/MyGlass_003.png",
      "Hiro_GlassFragment_001": "Cutins/MyFragment_001.png",
      "Hiro_GlassFragment_002": "Cutins/MyFragment_002.png",
      "Hiro_GlassFragment_003": "Cutins/MyFragment_003.png"
    },
    "Tints": {
      "Hiro_CutIn_StainedGlass_luminescence001": "#FFFFFF"
    },
    "HiddenLayers": ["Glow"],
    "MeshAlphaThreshold": 64,
    "Shaders": {
      "Background": {
        "PrimaryColor": "#00A060",
        "SecondaryColor": "#002010",
        "BlendFactor": 0.5
      },
      "StainedGlass": {
        "FlameColor": "#66FFCC",
        "CrackTexture": "none",
        "ShardTexture": "none"
      },
      "StainedGlassGlow": { "Color": "#CCFFEE" },
      "CharacterShadow":  { "Color": "#336655" },
      "CharacterGlow":    { "Color": "#CCFFEE" }
    },
    "Particles": { "Color": "#AAFFDD" }
  }
]
```

---

## 10. Appendix: internals (normally not needed)

### 10.1 How it works

The vanilla objection scene is a Naninovel spawn object (prefab `ObjectionCutIn_<Kind>`, `Kind` = `Hiro` / `Ema` / `CreatureHiro`) triggered by `@gosubCutIn`. The loader hijacks and swaps:

1. `@gosubCutIn "MyMod_XXX"` in the script is rewritten to the vanilla `Hiro`, so the game spawns Hiro's prefab normally.
2. After the prefab is created and before the animation starts, the loader swaps each layer's sprite and `SpriteRenderer.color` per `info.json`, sets `Renderer.enabled = false` on the layers listed in `HiddenLayers`, overrides material properties with a `MaterialPropertyBlock`, and overrides the particle renderer. The Timeline and the per-`index` pose switch only toggle `GameObject` active state, so a disabled renderer stays disabled for the whole scene.
3. The vanilla Hiro cut-in and all mod cut-ins share one instance; when a vanilla one plays, the loader restores every changed layer.

### 10.2 Limits

- Animation / timeline (keyframes of the Timeline and the four Animators) cannot be changed.
- Sound effects are played by the vanilla `System_Subroutine` script and the Timeline and cannot be changed; voice goes through the `voice:` parameter.
- `BaseTemplate` only supports `"Hiro"` (it is only the animation template; with your pictures it can be any character). The Ema / CreatureHiro prefabs are structured differently and are not adapted.
- Replacement pictures inherit the vanilla sprite's pivot and pixelsPerUnit; nothing is rescaled. Their mesh is built from the picture's own alpha (pixel-exact, alpha ≥ `MeshAlphaThreshold`, default 64), because Unity's runtime mesh leaves a 6–20 px margin that the glass shader would paint; if that step fails the log says `custom mesh ... failed` and the Unity mesh is used.

### 10.3 Prefab nodes and materials

| Prefab node path | Vanilla sprite | Material / shader |
| --- | --- | --- |
| `White_1` | `Background2` (development reference screenshot, not visible) | default sprite shader |
| `Image_Kari/*` | `Hiro_Background000`, `CutIN_Ema2`, `CutIN_Ema` (development reference screenshots, not visible) | default |
| `BackGround` | `Square` (Unity built-in; color comes from the shader) | `Background_Hiro` / `Shader Graphs/Background_0Fix` |
| `CutIN/Glow/RefuteCutIn_StainedGlassGlow_001..003` | `RefuteCutIn_StainedGlassGlow_001..003` | default |
| `CutIN/CutIN/RefuteCutIn_StainedGlass_luminescence` | `Hiro_CutIn_StainedGlass_luminescence001` (tint `#FFDEFF`) | `Iuminescence_dezolve_Hiro` / `Shader Graphs/Iuminescence_dezolve_0Fix` |
| `CutIN/CutIN/RefuteCutIn_StainedGlass_001..003` | `Hiro_CutIn_StainedGlass_001..003` | `Glasses_Fix_Hiro` / `Shader Graphs/Glasses_0Fix` |
| `Hiro/RefuteCutIn_Hiro_Shadow` | `Hiro_CutIn_ShadowWhite` | `Shadow_Fix` / `Shader Graphs/Shadow_Fix` |
| `Hiro/RefuteCutIn_Hiro_luminescence` | `Hiro_CutIn_luminescence` | `Iuminescence_Silhouette_Hiro` / `Shader Graphs/Iuminescence_Silhouette_0Fix` |
| `Hiro/RefuteCutIn_Hiro_001..003` | `Hiro_CutIn_001..003` | default |
| `Glass2/Root/GlassFragment_001..003` | `Hiro_GlassFragment_001..003` | default |
| `White` | `White` | default |
| `Glass` | — (ParticleSystem, material `Kirakira`, texture `kirakira`) | `Universal Render Pipeline/Particles/Unlit` |
| `Timeline`, `CutIN`, `CutIN/CutIN`, `Hiro`, `Glass2` | — (PlayableDirector / Animator) | — |

The `Shaders` sections match layers by shader name, not material name (material names carry a `_Hiro` suffix that changes with the template).

`HiddenLayers` matches against the vanilla sprite name and against every segment of the node path (the node itself and all of its parents inside the prefab), so `CutIN` covers both `CutIN/Glow/*` and `CutIN/CutIN/*`. The "groups" of §3.5 are these prefab nodes. The `BackGround` node keeps its `Square` sprite when hidden; only the renderer is switched off. Hiding uses `Renderer.enabled` rather than `GameObject.SetActive` because the Timeline and the `_objectLibrary` pose switch drive the active state and would turn a deactivated object back on.

The three texture slots of `Glasses_0Fix` sit on the material, not on the sprite, so `Sprites` cannot reach them; they are only reachable through `Shaders.StainedGlass`. The slot names are the reverse of the vanilla file names: `_kirakira` holds `..._kirakira2` (cracks) and `_kirakira2` holds `..._kirakira1` (highlights). The runtime dump is authoritative.

### 10.4 Vanilla asset sizes

| Name | Size |
| --- | --- |
| `Hiro_CutIn_001/002/003` | 1556×2048 |
| `Hiro_CutIn_luminescence` | 1603×2048 |
| `Hiro_CutIn_ShadowWhite` | 1625×2048 |
| `Hiro_CutIn_StainedGlass_001/002/003` | 2048×883 |
| `Hiro_CutIn_StainedGlass_003_kirakira1/2` | 2048×883 |
| `Hiro_CutIn_StainedGlass_luminescence001` | 2048×883 |
| `RefuteCutIn_StainedGlass_luminescence001` | 2048×883 |
| `RefuteCutIn_StainedGlassGlow_001/002/003` | 2048×1032 |
| `Hiro_GlassFragment_001/002/003` | 2048×1017 |
| `White` | 32×32 |
| `kirakira` | 128×128 |
