# 自定义 Cut-in（异议演出）配置指南

[English version](cutin.en.md)

这篇写给 mod 作者：要画哪些图、`info.json` 里怎么填、每个字段对应画面上的哪一块。不用懂 Unity。

---

## 0. 快速开始（先看这里）

**自定义 cut-in = 原版 Hiro 的异议动画 + 自己画的图。** 节奏、镜头、音效全部沿用原版，要做的只有两件事：把图画好，再在 `info.json` 里写清楚"哪张原版图换成哪张图"。各层的着色、背景色、玻璃特效、闪光粒子也能改，任意一层还可以整个关掉（§3.5），但都是可选项。

### 第一步：准备图片

图片放在 mod 文件夹（也就是 `info.json` 所在的那个文件夹）下随便哪个子文件夹里，比如 `Cutins/`，名字随意。要求：

- 透明底 **PNG**。JPG 没有透明通道，会带一块实心底色。
- **画布尺寸和原版完全一致**，角色也画在原版的位置上。最省事的办法是把原版图垫在底层照着画，画完把底层删掉；原版图怎么导出见 §2。

| 要画的 | 尺寸 | 画什么 |
| --- | --- | --- |
| 角色 ×3 | 1556×2048 | 三种表情。**三张的外形轮廓必须完全一样**，只能换脸，原因见 §3.3。只画一张也行，三个键都填同一张。 |
| 角色阴影 | 1625×2048 | 角色的白色实心剪影。实际显示颜色由配置决定，默认灰蓝。 |
| 角色发光 | 1603×2048 | 角色的白色柔边剪影。 |

只换了角色立绘、没换阴影和发光，原版 Hiro 的剪影就会从角色背后露出来。

### 第二步：在 `info.json` 顶层加 `CutIns`

和 `Characters`、`Clues` 这些放在同一级：

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

- `Id`：所有 mod 之间不能重名，建议拿 mod 名做前缀。
- `Sprites`：冒号左边是原版图片名，区分大小写，完整列表见 §3.1；右边是自己那张图的路径，从 mod 文件夹算起，分隔符用 `/`。
### 第三步：剧本里调用

```nani
@gosubCutIn "MyMod_MyChar" index:1
```

`index` 决定显示哪张角色立绘，**数字和图片编号是错开的**：

| 剧本里写 | 显示的是这个键下填的图 |
| --- | --- |
| `index:1` | `Hiro_CutIn_002` |
| `index:2` | `Hiro_CutIn_003` |
| `index:3` | `Hiro_CutIn_001` |

演出放完，剧本自动往下走。其它参数见 §7。

### 第四步：进游戏测试

图没显示出来，就打开游戏目录下的 `BepInEx/LogOutput.log`，搜 `[CutInLoader]`：文件找不到、图片读不出来、`Sprites` 里的键拼错，都会有一行警告指出具体是哪个。

### 一个 `CutIns` 条目里能写的所有字段

| 字段 | 管什么 | 详见 |
| --- | --- | --- |
| `Id` | 剧本里调用时用的名字。必填。 | §0 |
| `Sprites` | 哪张原版图换成你的哪张图。 | §3 |
| `Tints` | 给某一张图叠一个颜色。 | §3.4 |
| `HiddenLayers` | 把画面里的某些部分整个关掉。 | §3.5 |
| `MeshAlphaThreshold` | 自己画的玻璃带边缘发灰时用来调。一般不用写。 | §3.6 |
| `Shaders` | 背景色、玻璃特效、阴影和发光的颜色。 | §4 |
| `Particles` | 闪光粒子。 | §5 |
| `BaseTemplate` | 动画套哪一套原版 cut-in。只有 `"Hiro"` 一种，不用写。 | §10.2 |

---

## 1. 小词典

| 词 | 意思 |
| --- | --- |
| 原版 | 没装 mod 的游戏本体。 |
| 层 / sprite | cut-in 画面由几十张图叠成，每张图算一层；sprite 就是"这一层用的那张图"。 |
| 键（key） | `Sprites` 里冒号左边的原版图片名。 |
| 组 / 组名 | 几层打包在一起的名字，写组名能把整组一起关掉，见 §3.5。 |
| 透明度 / alpha | PNG 的透明通道：0 是全透明，255（也就是 100%）是完全不透明。 |
| 材质 / shader | 决定某一层怎么上色、怎么闪的一组设置。写配置时只会碰到字段名，不用碰材质本身。 |
| 遮罩 | 黑底白图，白色区域有效果，黑色区域没有。 |
| 粒子 | 画面上飞的小闪光点。 |
| dump | 调试模式下 loader 写进日志的一份清单，详见 §8。 |

---

## 2. 导出原版图当模板

原版图都在游戏的数据文件里，用免费工具 AssetRipper 就能导出成 PNG。

### 2.1 用 AssetRipper 导出

1. 到 GitHub 的 AssetRipper 发布页下载（搜 "AssetRipper github"，拿 Windows x64 的 zip），解压到任意位置。
2. 运行 `AssetRipper.GUI.Free.exe`，会在浏览器里打开一个页面。
3. 在页面里选 **File → Open Folder**，选中游戏安装目录（Steam 里右键游戏 → 管理 → 浏览本地文件）。
4. 等加载完成，选 **Export → Export Primary Content**，输出目录选一个空文件夹。
5. 图片在输出目录的 `Assets/Texture2D/` 里，文件名和 §3.1 的键完全一致（比如 `Hiro_CutIn_001.png`），玻璃遮罩和粒子贴图也在里面。

### 2.2 画图须知

- 所有图一律透明底 PNG。
- 画布尺寸和原版一致，角色放在原版的位置上。阴影图（1625 宽）和发光图（1603 宽）的画布比角色立绘（1556 宽）要宽，画之前分别拿各自的原版图垫底对齐。
- 三张角色立绘的外形轮廓必须完全一致，不然切换表情时会出现叠影（§3.3）。
- 阴影和发光图画成白色剪影就行，最终颜色由 `Shaders.CharacterShadow.Color` / `Shaders.CharacterGlow.Color` 决定（§4.4、§4.5）。
- 尺寸对不上不会自动缩放：图画大了在画面上就大，甚至出画；画小了就小。

---

## 3. `Sprites` — 换图片

### 3.1 可替换的层（全部键）

| 键（原版图片名） | 尺寸 | 画面上是什么 | 画什么 / 备注 |
| --- | --- | --- | --- |
| `Hiro_CutIn_001`<br>`Hiro_CutIn_002`<br>`Hiro_CutIn_003` | 1556×2048 | 角色立绘（三种表情） | 透明底全彩图，三张轮廓要一致。见 §3.3。 |
| `Hiro_CutIn_ShadowWhite` | 1625×2048 | 角色投影 | 白色实心剪影。颜色由 `Shaders.CharacterShadow.Color` 决定。 |
| `Hiro_CutIn_luminescence` | 1603×2048 | 角色发光 | 白色柔边剪影。颜色由 `Shaders.CharacterGlow.Color` 决定。 |
| `Hiro_CutIn_StainedGlass_001`<br>`Hiro_CutIn_StainedGlass_002`<br>`Hiro_CutIn_StainedGlass_003` | 2048×883 | 斜穿画面的彩色玻璃带，三张略有差别，动画里先后出现 | 透明底全彩图。**画法上有个讲究：**玻璃图不支持半透明，不透明度超过大约 25% 的像素会画成完全不透明，低于的完全不画。所以边缘不要羽化，透明区域也不要留半透明的杂点，不然会出现硬边和灰色斑点（这个 25% 可以调，见 §3.6）。完全透明的像素底下是什么颜色无所谓。玻璃上的火焰、裂纹、高光是另外叠上去的，在 `Shaders.StainedGlass` 里改（§4.2）。 |
| `Hiro_CutIn_StainedGlass_luminescence001` | 2048×883 | 玻璃带的发光 / 溶解层 | 白色玻璃带剪影。**这个名字里 `001` 前面没有下划线，别写错。** 颜色由 `Shaders.StainedGlassGlow` 决定（§4.3）。 |
| `RefuteCutIn_StainedGlassGlow_001`<br>`RefuteCutIn_StainedGlassGlow_002`<br>`RefuteCutIn_StainedGlassGlow_003` | 2048×1032 | 玻璃带后面的粉色柔光 | 淡粉色柔边剪影。名字里虽然带 StainedGlassGlow，但既**不是**上一行那层，也不归 `Shaders.StainedGlassGlow` 管。 |
| `Hiro_GlassFragment_001`<br>`Hiro_GlassFragment_002`<br>`Hiro_GlassFragment_003` | 2048×1017 | 飞散的玻璃碎片，三组，位移由动画驱动 | 透明底碎片图。 |
| `White` | 32×32 | 全屏白闪 | 一般不用动。想去掉白闪，把 `White` 写进 `HiddenLayers`（§3.5）。 |

prefab 里还有几层开发时留下的参考截图（`Background2`、`Hiro_Background000`、`CutIN_Ema`、`CutIN_Ema2`），游戏里不显示，不用管。

### 3.2 规则

- 值填相对 mod 文件夹的图片路径（可以带子文件夹，分隔符用 `/`），或者 `"none"`。
- 填 `"none"`，这一层就换成一张全透明图。能用，但要关掉一整层，`HiddenLayers`（§3.5）是更好的办法。
- 没写的键保持原版。
- 文件不存在或读不出来 → 日志里报警告，这一层保持原版。
- 键拼错（大小写错也算）→ 日志里报警告 `these Sprites keys match no layer`，这个键直接忽略。
- 每张替换图都会沿着自己的轮廓裁一刀，多透明算"轮廓外"由 `MeshAlphaThreshold` 决定，见 §3.6。

### 3.3 `index` 与三张角色立绘

`index:1 / 2 / 3` 分别显示 `Hiro_CutIn_002 / 003 / 001`，对照表在 §0 第三步。

同一场审判里要是先后用了不同的 `index`，**之前显示过的角色立绘不会关掉**，几张图会叠在一起。原版三张图外形完全一样，叠了也看不出来；但只换其中一张的话，没换的原版 Hiro 就会从背后露出来。所以三张都得填，三种方案选一个：

- 三张不同表情，外形轮廓完全一致；
- 三张填同一张图；
- 用不到的填 `"none"`（或写进 `HiddenLayers`，§3.5），剧本里也别用对应的 `index`。

### 3.4 `Tints` — 改某一层的着色

每一层除了图片本身，还带一个"着色"，也就是一个和图片相乘的颜色。原版几乎所有层都是白色，等于不改动图片；只有玻璃带发光层 `Hiro_CutIn_StainedGlass_luminescence001` 是淡粉 `#FFDEFF`。键和 `Sprites` 一样，值写 HTML 颜色：

```json
"Tints": {
  "Hiro_CutIn_StainedGlass_luminescence001": "#FFFFFF",
  "Hiro_CutIn_001": "#C0C0FF"
}
```

- 着色是乘上去的：白色 = 原样，`#FF8080` = 整体偏红、其它通道压暗，带 `AA` 的颜色能让整层半透明。
- 没写的层保持原版着色；切回原版 cut-in 时会自动恢复。
- 有些层的着色归演出动画管，比如 `White` 白闪的透明度，动画改到的那部分会盖掉配置里的值。

### 3.5 `HiddenLayers` — 整个关掉

cut-in 里的任何一部分都能整个关掉：单独一张图、一整组图、闪光粒子，或者全屏背景。写法是一个名字列表：

```json
"HiddenLayers": ["BackGround", "Glass2", "White"]
```

**名字从哪来。** cut-in 的几十层是分组放的，就像文件放在文件夹里，比如"角色"这一组里装着立绘、阴影、发光。列表里既可以写单独一层的名字（就是 `Sprites` 的键），也可以写组名，写组名就把整组一起关掉。名字不分大小写（这点和 `Sprites` 不一样）。全部分组如下，缩进表示包含关系：

```
BackGround   全屏背景（红黑噪点渐变）
CutIN        整条玻璃带连同它的光
  Glow         玻璃带后面的粉色柔光（RefuteCutIn_StainedGlassGlow_001~003）
  CutIN        玻璃带本体三张 + 发光 / 溶解层 Hiro_CutIn_StainedGlass_luminescence001
Hiro         角色立绘三张 + 阴影 + 角色发光
Glass2       飞散的玻璃碎片三组
Glass        闪光粒子（名字是游戏起的，它管的不是玻璃）
White        全屏白闪
```

这些名字在 §8 的日志里也能看到。常用写法：

| 想关掉什么 | 写什么 |
| --- | --- |
| 全屏背景 | `BackGround` |
| 角色立绘、阴影、角色发光一起关 | `Hiro`（这是组名，跟你换没换图无关，你的角色也在这一组里） |
| 只关角色立绘，留下阴影和发光 | `Hiro_CutIn_001`、`Hiro_CutIn_002`、`Hiro_CutIn_003` |
| 只关角色阴影 | `Hiro_CutIn_ShadowWhite` |
| 只关角色发光 | `Hiro_CutIn_luminescence` |
| 整条玻璃带，连同它的发光 / 溶解层和后面的粉色柔光 | `CutIN` |
| 只关玻璃带本体，留下两种光 | `Hiro_CutIn_StainedGlass_001`、`Hiro_CutIn_StainedGlass_002`、`Hiro_CutIn_StainedGlass_003` |
| 只关玻璃带的发光 / 溶解层 | `Hiro_CutIn_StainedGlass_luminescence001` |
| 只关玻璃带后面的粉色柔光 | `Glow` |
| 飞散的玻璃碎片 | `Glass2` |
| 闪光粒子 | `Glass` |
| 全屏白闪 | `White` |

**三个容易写混的名字：**`Glass` 是闪光粒子，`Glass2` 是玻璃碎片，玻璃带本身是 `CutIN`。

关掉之后画面上是什么样：

- 关掉的部分不会画出来。就算你在 `Sprites`、`Tints`、`Shaders` 里也给它写了东西，也不会报错，只是看不见。
- 整场演出都不会再出现，动画不会中途把它打开。
- 背景关掉之后，露出来的是演出开始前画面上原本的东西，一般就是法庭场景。背景没有对应的图，换不了自己画的背景，只能改颜色（§4.1）或者关掉。
- 没写的部分保持原版；切回原版 cut-in 时全部自动恢复。
- 名字什么都没对上，日志里会报警告 `these HiddenLayers entries match no layer or node`，这个名字直接忽略。

**三种关法怎么选：**

| 想去掉的是 | 写法 | 说明 |
| --- | --- | --- |
| 任意一层、一整组，或者背景 | `"HiddenLayers": [ ... ]` | 首选。什么都不画，也不用多加载图。 |
| 单独一层 | `"Sprites": { "键": "none" }` | 看起来一样，但要多加载一张透明图，而且对背景没用。 |
| 玻璃上的裂纹、碎片高光 | `"Shaders": { "StainedGlass": { "CrackTexture": "none" } }` 之类 | 只能这么去。它们是叠在玻璃上的特效，不是单独的层（§4.2）。 |
| 闪光粒子 | `"HiddenLayers": ["Glass"]` | 和第一行是同一个机制；`Glass` 是粒子发射器所在的节点名。 |

### 3.6 `MeshAlphaThreshold` — 玻璃带边缘发灰怎么办

只有自己画的玻璃带周围出现了一圈灰边或者一些灰点，才需要碰这个字段。没这个问题就别写。

为什么会发灰：玻璃带这一层不认图片的透明度，只要在图的轮廓以内，不管像素多淡，一律画成实心玻璃。轮廓是游戏自动算的，可它会在画的外面多留几个像素的余量，小缺口也会被抹平，余量里那些"透明"像素就跟着画出来了。原版图是专门处理过的，看不出来；换成自己的图，就成了灰边和灰点。

加载器怎么解决：改成按你 PNG 的透明通道逐像素描轮廓。`MeshAlphaThreshold` 就是描线的标准：不透明度达到这个数的像素算轮廓以内，会画出来；比它淡的算轮廓以外，完全不画。取值 0 到 255，默认 64，差不多 25%。

```json
"MeshAlphaThreshold": 64
```

对画图的影响：

- 玻璃带：边缘画实，空白处擦干净。羽化的边会在这条线的位置变成硬边，空白处残留的淡像素会变成灰点。还有灰点就把数调高（比如 128）；画上的细尖被切掉了就调低（比如 32）。
- 其它换掉的图也按同一条线裁，不过它们本来就认透明度，所以唯一的区别是比这条线淡的像素不见了。特别柔的发光图可能会觉得最外圈少了一点淡边，介意的话把数调低。
- 一个 cut-in 只有一个值，对它的所有替换图都生效。

---

## 4. `Shaders` — 颜色与特效

只写想改的字段，没写的保持原版。这一整节都是可选的。

- 颜色写 `"#RRGGBB"` 或 `"#RRGGBBAA"`，不写 `AA` 就是不透明，`#` 也可以不写。
- 数值写小数。
- 贴图字段写相对 mod 文件夹的图片路径，或 `"none"`。
- 标"高级"的字段一般不用动。
- "原版值"是按 §8 的方法从 Hiro 的材质上实际读出来的。标"动画驱动"的字段在演出过程中会自己变，表里给的是静止值；一旦覆盖，动画就会定在那个值上。
- "内部名"只在读 §8 的 dump 时用来对照，平时不用管。

### 4.1 `Background` — 全屏背景

原版是红黑噪点渐变。

| 字段 | 作用 | 原版值 | 内部名 |
| --- | --- | --- | --- |
| `PrimaryColor` | 背景主色 | `#FF3C45` | `_BackgroundA` |
| `SecondaryColor` | 背景副色，按噪点和主色混合 | `#000000` | `_BackgroundB` |
| `BlendFactor` | 两色混合 / 噪点强度。高级。 | 0.3 | `_Float` |

```json
"Shaders": { "Background": { "PrimaryColor": "#1E90FF", "SecondaryColor": "#001030" } }
```

### 4.2 `StainedGlass` — 彩色玻璃带上的特效

玻璃图本身走 `Sprites` 换（§3.1），这里改的是叠在玻璃上面的火焰扫光和遮罩。

| 字段 | 作用 | 原版值 | 内部名 |
| --- | --- | --- | --- |
| `FlameColor` | 扫过玻璃的火焰 / 边缘光颜色 | `#FF000B` | `_EclipseFlame` |
| `CrackTexture` | 玻璃表面的细裂纹线。`"none"` = 去掉裂纹。 | 原版图 `Hiro_CutIn_StainedGlass_003_kirakira2`（黑底白线，2048×883） | `_kirakira` |
| `ShardTexture` | 碎片块的高光。`"none"` = 去掉高光。 | 原版图 `Hiro_CutIn_StainedGlass_003_kirakira1`（黑底白块，2048×883） | `_kirakira2` |
| `GlowMaskTexture` | 目前没有作用，填了也看不出变化，留着只是和另外两个凑齐，不用管。 | 原版图 `RefuteCutIn_StainedGlass_luminescence001`（黑底白色玻璃带剪影，2048×883） | `_luminescence` |
| `Fader` | 扫光阈值 1（0–1）。高级，动画驱动。 | 1 | `_Fader` |
| `Fader2` | 扫光阈值 2（0–1）。高级，动画驱动。 | 1 | `_Fader2` |
| `Speed` | 扫光速度，越大越快。高级。 | 3 | `_Speed` |
| `Tick` | 扫光动画相位（0–1）。高级，动画驱动。 | 1 | `_Tick` |
| `EdgeSize` | 边缘光宽度，越大越宽。高级。 | 0.02 | `_EdgeSize` |

自定义遮罩做成和玻璃图同尺寸（2048×883）的黑底白图。

```json
"Shaders": { "StainedGlass": { "CrackTexture": "none", "ShardTexture": "none", "FlameColor": "#66FFCC" } }
```

### 4.3 `StainedGlassGlow` — 玻璃带发光层

对应 `Hiro_CutIn_StainedGlass_luminescence001` 这一层。

| 字段 | 作用 | 原版值 | 内部名 |
| --- | --- | --- | --- |
| `Color` | 发光颜色。实际显示 = 这里填的颜色 × 这一层的着色。着色原版是淡粉 `#FFDEFF`，可以用 §3.4 的 `Tints` 改成白色。 | `#FDA5A4` | `_Color` |
| `FlameColor` | 溶解边缘的火焰色 | `#FF000B` | `_EclipseFlame` |
| `Tick` | 溶解动画相位（0–1）。高级，动画驱动。 | 0.952 | `_Tick` |

### 4.4 `CharacterShadow` — 角色投影

对应 `Hiro_CutIn_ShadowWhite` 这一层。

| 字段 | 作用 | 原版值 | 内部名 |
| --- | --- | --- | --- |
| `Color` | 投影颜色 | `#737D99`（灰蓝） | `_Color` |

### 4.5 `CharacterGlow` — 角色发光

对应 `Hiro_CutIn_luminescence` 这一层。

| 字段 | 作用 | 原版值 | 内部名 |
| --- | --- | --- | --- |
| `Color` | 发光颜色 | `#FDA5A4` | `_Color` |
| `Tick` | 动画相位（0–1）。高级。 | 0.119 | `_Tick` |

---

## 5. `Particles` — 闪光粒子

画面上飞的小星光。这里只能改外观，数量、速度、寿命动不了。

换贴图 / 颜色：

```json
"Particles": { "Texture": "Cutins/my_sparkle.png", "Color": "#AAFFFF" }
```

想把粒子整个关掉，不要写在 `Particles` 里，改写 `"HiddenLayers": ["Glass"]`（§3.5）。`"Texture": "none"` 效果一样，只是更费。

| 字段 | 作用 | 原版值 | 内部名 |
| --- | --- | --- | --- |
| `Texture` | 每个粒子的图 | 原版图 `kirakira`（128×128，透明底白色十字星光） | `_BaseMap` |
| `Color` | 颜色乘数，和粒子自带的颜色相乘 | 白 | `_BaseColor` |

---

## 6. `"none"` 能写在哪里

下面这些地方写 `"none"`（不分大小写），都会换成一张全透明图：

- `Sprites` 里任意一个键的值 → 隐藏那一层（关一整层更推荐用 `HiddenLayers`，§3.5）。
- `Shaders.StainedGlass` 的 `CrackTexture`、`ShardTexture` → 去掉对应效果。
- `Particles.Texture` → 粒子看不见了，不过不如直接写 `"HiddenLayers": ["Glass"]`。

颜色和数值字段不认 `"none"`，不想改就别写。

`HiddenLayers` 和 `"none"` 各用在什么地方，见 §3.5 末尾那张表。

---

## 7. 剧本用法（完整参数）

```nani
@gosubCutIn "MyMod_MyChar" index:1
@gosubCutIn "MyMod_MyChar" index:2 voice:"MyMod_Voice/objection_01" volume:1
```

| 参数 | 必填 | 说明 |
| --- | --- | --- |
| （无名参数） | 是 | Cut-in 的 `Id`。 |
| `index` | 是 | 1 / 2 / 3，决定显示哪张角色立绘，对应关系见 §3.3。 |
| `voice` | 否 | 配音资源路径，写法和 mod 里其它语音一样。 |
| `volume` | 否 | 配音音量，默认 1。 |
| `group` | 否 | 音频混音组。高级，通常不填。 |
| `authorId` | 否 | 说话角色 ID。高级，通常不填。 |

演出放完，剧本自动往下走。

---

## 8. 调试模式：读原版精确值

1. 打开游戏目录下的 `BepInEx/config/ManosabaLoader.cfg`（游戏启动过一次才会生成），在 `[Debug]` 段里把 `OpenDebug = false` 改成 `OpenDebug = true`。
2. mod 里至少要有一个 cut-in 条目（只写 `Id` 也行），剧本里要有一句 `@gosubCutIn` 调用它。启动游戏，把剧情推进到那一句。
3. 打开游戏目录下的 `BepInEx/LogOutput.log`，搜 `First mod cut-in spawn`。
4. 每层一行，长这样：
   `SpriteRenderer  Hiro/RefuteCutIn_Hiro_001 -> vanilla=Hiro_CutIn_001 now=ModCutIn_MyMod_MyChar_Hiro_CutIn_001 color=#FFFFFFFF active=True enabled=True sorting=Default:300 ...`
   - 行首的 `Hiro/RefuteCutIn_Hiro_001` 是这层的位置，用 `/` 隔开的每一段都能写进 `HiddenLayers`，这里就是组名 `Hiro` 和层名 `RefuteCutIn_Hiro_001`。
   - `vanilla=` 后面是 `Sprites` 和 `Tints` 里要写的键。
   - `color=` 是这层当前的着色。
   - `enabled=False` 表示已经被 `HiddenLayers` 关掉。
   - `active=` 是动画当前有没有把它打开，看看就行。
5. 再往下是材质行，比如
   `Background_Hiro (Shader Graphs/Background_0Fix): _Float=0.3 [Range], _BackgroundA=#FF3C4500 [Color], _BackgroundB=#00000000 [Color], ...`
   颜色格式是 `#RRGGBBAA`，可以直接抄进 JSON；末尾两位 `AA` 是透明度，这些特效用不到，抄的时候去掉也行。`_BackgroundA` 这类内部名，对着 §4 各表的"内部名"列就能找回 `PrimaryColor` 这些字段。
6. 想再打印一次，得回到标题画面重新进审判。用完记得把 `OpenDebug` 改回 `false`。

---

## 9. 完整示例

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

## 10. 附录：内部结构（一般不用看）

### 10.1 工作原理

原版异议演出是一个 Naninovel spawn 对象，prefab 叫 `ObjectionCutIn_<Kind>`，`Kind` = `Hiro` / `Ema` / `CreatureHiro`，由 `@gosubCutIn` 触发。Loader 的思路是劫持 + 替换：

1. 把剧本里的 `@gosubCutIn "MyMod_XXX"` 改写成原版的 `Hiro`，让游戏照常生成 Hiro 的 prefab。
2. prefab 生成之后、动画开始之前，按 `info.json` 换掉各层的 sprite 和 `SpriteRenderer.color`，把 `HiddenLayers` 列出的层设成 `Renderer.enabled = false`，用 `MaterialPropertyBlock` 覆盖材质属性，再覆盖粒子渲染器。Timeline 和按 `index` 切 pose 只动 `GameObject` 的激活状态，所以关掉的渲染器整场演出都保持关闭。
3. 原版 Hiro cut-in 和所有 mod cut-in 共用同一个实例，切回原版时 loader 会把改过的层全部还原。

### 10.2 限制

- 动画和时间轴（Timeline 以及 4 个 Animator 的关键帧）改不了。
- 音效由原版 `System_Subroutine` 脚本和 Timeline 播放，改不了；配音走 `voice:` 参数。
- `BaseTemplate` 只支持 `"Hiro"`。这只是动画模板，图换掉之后想做成谁都行。Ema / CreatureHiro 的 prefab 结构不一样，还没适配。
- 替换图沿用原版 sprite 的 pivot 和 pixelsPerUnit，不做缩放。网格由图片自己的 alpha 生成（精确到像素，alpha ≥ `MeshAlphaThreshold`，默认 64），因为 Unity 运行时自动生成的网格在轮廓外留有 6–20 px 余量、会被玻璃 shader 画出来；这一步失败时日志会出现 `custom mesh ... failed` 并回退到 Unity 网格。

### 10.3 prefab 节点与材质

| prefab 节点路径 | 原版 sprite | 材质 / shader |
| --- | --- | --- |
| `White_1` | `Background2`（开发参考截图，不显示） | 默认 sprite shader |
| `Image_Kari/*` | `Hiro_Background000`、`CutIN_Ema2`、`CutIN_Ema`（开发参考截图，不显示） | 默认 |
| `BackGround` | `Square`（Unity 内置方块，颜色由 shader 生成） | `Background_Hiro` / `Shader Graphs/Background_0Fix` |
| `CutIN/Glow/RefuteCutIn_StainedGlassGlow_001..003` | `RefuteCutIn_StainedGlassGlow_001..003` | 默认 |
| `CutIN/CutIN/RefuteCutIn_StainedGlass_luminescence` | `Hiro_CutIn_StainedGlass_luminescence001`（tint `#FFDEFF`） | `Iuminescence_dezolve_Hiro` / `Shader Graphs/Iuminescence_dezolve_0Fix` |
| `CutIN/CutIN/RefuteCutIn_StainedGlass_001..003` | `Hiro_CutIn_StainedGlass_001..003` | `Glasses_Fix_Hiro` / `Shader Graphs/Glasses_0Fix` |
| `Hiro/RefuteCutIn_Hiro_Shadow` | `Hiro_CutIn_ShadowWhite` | `Shadow_Fix` / `Shader Graphs/Shadow_Fix` |
| `Hiro/RefuteCutIn_Hiro_luminescence` | `Hiro_CutIn_luminescence` | `Iuminescence_Silhouette_Hiro` / `Shader Graphs/Iuminescence_Silhouette_0Fix` |
| `Hiro/RefuteCutIn_Hiro_001..003` | `Hiro_CutIn_001..003` | 默认 |
| `Glass2/Root/GlassFragment_001..003` | `Hiro_GlassFragment_001..003` | 默认 |
| `White` | `White` | 默认 |
| `Glass` | —（ParticleSystem，材质 `Kirakira`，贴图 `kirakira`） | `Universal Render Pipeline/Particles/Unlit` |
| `Timeline`、`CutIN`、`CutIN/CutIN`、`Hiro`、`Glass2` | —（PlayableDirector / Animator） | — |

`Shaders` 各节按 shader 名匹配层，不看材质名，因为材质名带 `_Hiro` 后缀，换了模板就会变。

`HiddenLayers` 同时对照原版 sprite 名和节点路径的每一段（节点自己以及 prefab 内的所有上级），所以 `CutIN` 能同时盖住 `CutIN/Glow/*` 和 `CutIN/CutIN/*`。§3.5 里说的"组"就是这里的节点。`BackGround` 节点关掉后仍然挂着 `Square` sprite，只是渲染器被关了。关层用的是 `Renderer.enabled` 而不是 `GameObject.SetActive`，因为 Timeline 和 `_objectLibrary` 切 pose 都在操作激活状态，SetActive(false) 会被翻回来。

`Glasses_0Fix` 的三个贴图槽挂在材质上，不在 sprite 上，所以 `Sprites` 换不到，只能走 `Shaders.StainedGlass`。槽名和原版文件名的 1/2 正好是反的：`_kirakira` 里装的是 `..._kirakira2`（裂纹），`_kirakira2` 里装的是 `..._kirakira1`（高光）。一切以运行时 dump 为准。

### 10.4 原版素材尺寸

| 名字 | 尺寸 |
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
