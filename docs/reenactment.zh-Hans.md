# 自定义再现演出（舞台场景）配置指南

[English](reenactment.en.md)

> **状态：实验性。** 已用验证 mod 在游戏内跑通；JSON 格式仍可能根据反馈调整。

写给 mod 作者：如何制作自己的"再现演出"——原版审判中那种拉开幕布、在舞台上重演案情的场景（角色、道具、飞出去的箭……）。你可以放任意图片、自己摆位置、用关键帧做动画，不需要 Unity。

---

## 0. 快速上手

**你的再现 = 原版幕布外壳 + 你的图层 + 你的关键帧动画。** 黑边、幕布及其开闭动画来自一段原版场景，舞台上的东西全部由你决定。

### 第一步：准备图片

把 PNG（需要时用透明背景）放到 mod 目录下任意子文件夹，例如 `Reenact/`。

- 舞台坐标用**世界单位**。多少像素算 1 单位由每个图层的 `PixelsPerUnit` 决定（默认 100）。
- 镜头能看到约 **27 × 15 单位**；左右各有一条不透明黑边，横向约 ±12 单位以外都被挡住，所以可用舞台约 **24 × 15 单位**。舞台中心是 `(0, 0)`，x 向右、y 向上。
- 原版的做法（照抄即可）：2048×1024 的背景用 `PixelsPerUnit` 50、缩放 0.7，铺满 28.7 × 14.3 单位；角色和道具（几百像素）用 `PixelsPerUnit` 100、缩放 0.7。
- 图片轴心默认在中心；位置、缩放、旋转都围绕轴心。

### 第二步：在 `info.json` 顶层加入 `Reenactments`

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

### 第三步：在剧本里调用

```nani
@set "isReenact = true"                 ; 原版标记：再现期间 BeginAdv 不显示魔女图鉴按钮
@printer Normal.Simple time:0
@gosub System/System_Subroutine.BeginAdv

@spawn "MyMod_Scene1" params:0 !wait     ; 开幕并开始播放第 0 步，剧本不等待
@wait 1.5                                ; 盖住开幕动画（约 1.2 秒）
Hero: 事情就是这样发生的。               ; 普通对话，叠在打开的舞台上，照常等玩家点击
Hero: 然后就结束了。
@resetText                               ; 闭幕前清空文本框
@despawn "MyMod_Scene1" !wait            ; 闭幕（2 秒）并移除舞台
@wait 1.5

@set "isReenact = false"
```

这就是原版审判剧本的写法（取自游戏自带的 Act01_Chapter01_Trial15）：spawn 和 despawn 都不等待（`!wait`），用固定的 `@wait` 盖住幕布动画，对话打印在打开的舞台上，闭幕前用 `@resetText` 清空文本框。原版甚至在 spawn 之后紧接着放了一个 `@autoSave`。`params:N` 选择步骤（从 0 开始）；要在同一个打开的舞台上播放下一步，直接再写一次带不同 `params:` 的 `@spawn`，中间不要 `@despawn`。如果需要剧本卡住等动画播完，把 `!wait` 换成 `wait:true`。

### 第四步：游戏内测试

打开 `BepInEx/LogOutput.log` 搜索 `[ReenactLoader]`：

- `Registered mod reenactment 'MyMod_Scene1'` —— 场景已就绪。
- `SetSpawnParameters on 'MyMod_Scene1' ... armed` —— 剧本已经走到这里。
- 警告会指出：图片缺失、图层名拼错、颜色格式错误、步骤序号越界、模板一直没加载出来。

---

## 1. `Reenactments[]` 字段

| 字段 | 必填 | 含义 |
| --- | --- | --- |
| `Id` | 是 | `@spawn` / `@despawn` 用的路径。所有 mod 之间必须唯一，建议加 mod 名前缀。 |
| `Template` | 否 | 复用哪段原版场景的外壳（黑边、幕布、开闭幕动画）。默认 `Reenact_Ch01_E01_S02`，见 §4。 |
| `Layers` | 是 | 舞台上的图片（§2）。 |
| `Steps` | 否 | 动画步骤（§3）。省略时视为一步、1 秒、无动画。 |

## 2. `Layers[]` 字段

| 字段 | 默认 | 含义 |
| --- | --- | --- |
| `Name` | — | 本再现内唯一的图层名，轨道通过它引用。 |
| `Sprite` | — | 图片路径，相对 mod 目录，用 `/` 分隔。 |
| `Position` | `[0, 0]` | `[x, y]`，世界单位。 |
| `Scale` | `1` | 一个数（等比）或 `[x, y]`。原版素材按 0.7 绘制。 |
| `Rotation` | `0` | 角度，逆时针为正。 |
| `Pivot` | `[0.5, 0.5]` | 图片内轴心，0–1，从左下角起算。 |
| `Alpha` | `1` | 不透明度 0–1。 |
| `Tint` | 白 | 乘到图片上的 HTML 颜色。 |
| `Order` | `0` | 叠放顺序，大的在上；相同时按声明顺序。 |
| `Visible` | `true` | 初始可见性。 |
| `PixelsPerUnit` | `100` | 这张图多少像素算 1 世界单位。整幅 2048×1024 舞台背景用 50，角色和道具用 100（原版取值）。 |

图层按声明顺序创建，状态在步骤之间保留。

## 3. `Steps[]`、`Tracks[]` 与 `Keys[]`

- `Steps[i].Duration` —— 秒。关键帧动画在这段时间内播放。用 `!wait`（原版写法）时剧本立刻往下走，动画在对话期间播放；用 `wait:true` 时剧本会等到开幕动画加本时长结束。
- `Steps[i].Tracks[]` —— 每个要动的图层一条：`{ "Layer": "<名字>", "Keys": [ ... ] }`。没写轨道的图层保持当前状态。
- 每个关键帧有 `Time`（相对本步开始的秒数）和下面任意几个属性，只写要变的。

| 关键帧属性 | 插值 | 说明 |
| --- | --- | --- |
| `Position` `[x, y]` | 是 | |
| `Scale` 数或 `[x, y]` | 是 | |
| `Rotation` | 是 | 角度 |
| `Alpha` | 是 | 0–1 |
| `Tint` | 是 | HTML 颜色 |
| `Visible` | 否 | 到该时间点切换 |
| `Sprite` | 否 | 到该时间点换图（相对 mod 目录的路径） |
| `Ease` | — | 从上一个定义了同一属性的关键帧到**本**关键帧的缓动 |

插值规则：每个属性在相邻两个定义了它的关键帧之间插值；第一个定义它的关键帧之前保持该帧的值，最后一个之后保持不变。因此单独一条 `{ "Time": 0.5, "Visible": true }` 只会让图层在 0.5 秒出现，不会碰它的位置。

`Ease` 取值：`Linear`（默认）、`InQuad`、`OutQuad`、`InOutQuad`、`InCubic`、`OutCubic`、`InOutCubic`、`InSine`、`OutSine`、`InOutSine`、`Step`（保持上一个值，到时间点再跳变）。

## 4. 模板

任何一段原版再现都可以当外壳。56 段共用同样的幕布和黑边，舞台内容会被丢弃，所以除非外壳有你在意的差别，选哪个都一样。命名规则 `Reenact_Ch<幕>_E<章>_S<场>`，例如：

`Reenact_Ch01_E01_S01` … `S06`、`Reenact_Ch01_E02_S01` … `S05`、`Reenact_Ch01_E03_S01` … `S06`、`Reenact_Ch01_E04_S01` … `S04`、`Reenact_Ch01_E05_S01` … `S08`、`Reenact_Ch02_E01_S01` … `S05`、`Reenact_Ch02_E02_S01` … `S06`、`Reenact_Ch02_E03_S01` … `S06`、`Reenact_Ch02_E04_S01` … `S05`、`Reenact_Ch02_E06_S01` … `S05`。

在 `BepInEx/config/ManosabaLoader.cfg` 里把 `Debug.OpenDebug = true`，加载器会把每个加载到的模板的完整层级（节点、Sprite、排序、director）打到日志里。

## 5. 获取原版图层素材

原版舞台图层就是游戏数据里的普通 PNG（以 1-1-2 场为例：`1-1-2_Leia.png`、`1-1-2_Bow.png`、`1-1-2_Arrow.png`、`Background_1-1-2.png`），用 AssetRipper 按 cut-in 指南 §2 的方法导出即可。它们很适合当尺寸和位置参考：原版 1-1-2 场里（在运行中的游戏里读到的数值）背景在 `(0, 0)`、莱娅 `(-0.01, -0.48)`、弓 `(2.42, -1.55)`、箭 `(-2.08, -0.71)`，缩放都是 0.7，背景的 `PixelsPerUnit` 是 50、其余是 100。原版舞台图层的排序在 -120 到 0 之间，上面压着一层半透明的黑色遮罩；你的 `Order` 会加到原版基准排序上，建议保持在 0 到 100 之间。`*_kari` 那些整图是开发期占位（"仮"），游戏里不会显示。

## 6. 行为说明与限制

- 存档读档由游戏自身的 spawn 状态还原，读档后当前步骤显示为结束姿态。
- 快进时舞台直接跳到本步骤的结束姿态。
- 场景是一个 spawn 对象：切换到下一个场景前必须先 `@despawn`（闭幕），和原版剧本一样。
- 外壳的幕布、黑边及其动画不可修改。
- 不支持骨骼动画；把角色拆成几部分分别做动画即可。
- 原版参考：原版每段场景是一次 spawn、只有一条剧情 Timeline；同一舞台上的多个 `Steps` 是本加载器额外提供的。其余（标记变量、等待、`@resetText`）见第三步。
- 关于 `isReenact`：游戏里只有 `BeginAdv` 子程序读它，条件是 `hasWitchBook && !isReenact` 时才显示魔女图鉴按钮。在你的演出前后把它设为 `true` / `false`，舞台打开期间图鉴按钮就会像原版审判一样保持隐藏；如果你的 mod 从不发放魔女图鉴，它没有任何效果，但照抄没有成本，也能兼容游戏以后的逻辑。
