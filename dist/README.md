# DSP Miner Multiplier（DSP 采矿机倍率模组）

戴森球计划（Dyson Sphere Program）BepInEx 模组：

- ⛏️ **采矿倍率**：可自由设置，范围 **1 ~ 1000 倍**
- ⚡ **耗电线性增长**：耗电随采矿倍率线性同步，公式

  ```
  耗电倍率 = 1 + (采矿倍率 − 1) × 255 / 999
  ```

  即：采矿 1 倍 → 耗电 1x；采矿 1000 倍 → 耗电正好 **256x**（上限 256 倍）。

| 采矿倍率 | 耗电倍率 |
|---:|---:|
| 1x | 1x |
| 10x | 3.29x |
| 100x | 26.53x |
| 500x | 128.53x |
| 1000x | 256x |

适用于**采矿机、巨型采矿机**（以及同类 `MinerComponent` 实体，如抽水机）。

## 游戏内快捷键

游戏中随时按 **Alt+F1** 打开/关闭倍率调节面板：

- 滑条或快捷按钮（1x / 10x / 100x / 500x / 1000x）即时调整倍率
- 也可手动输入任意数值（1 ~ 1000）
- 面板显示当前采矿倍率与对应耗电倍率
- 修改会自动保存到配置文件

组合键可在配置文件 `[Hotkey]` 段修改（`修饰键 ModifierKey` + `切换键 ToggleKey`）。

## Nebula 联机同步

安装 [Nebula](https://dsp.thunderstore.io/package/nebula/NebulaMultiplayerMod/) 与
[NebulaMultiplayerModApi](https://dsp.thunderstore.io/package/nebula/NebulaMultiplayerModApi/) 后自动启用：

- 联机开始时，**主机**把当前倍率广播给所有客户端
- 游戏中主机通过 Alt+F1 面板修改倍率，客户端实时跟随
- 新玩家中途加入时，主机自动重发当前倍率
- 客户端本地改倍率不影响其他玩家（以主机为准）
- 联机结束后恢复本机配置的倍率

不装 Nebula 时模组照常单机运行。

## 显示与实际一致

以下位置的产量/耗电显示会按倍率同步缩放（避免显示与实际不符）：

- 采矿机窗口（产量/分，耗电本身按实际 requiredEnergy 显示）
- 建造预览 gizmo（`N x XX/min`）
- 物品详情属性（矿机"每分每矿脉"等描述）
- 采集器面板（矿脉/海洋采集器的速度文本）
- 配电面板中巨型采矿机条目的额定功率
- 星球/恒星详情面板中的水泵产量（`N /min` 条目，气态采集率不受影响）

## 实现原理（已对 Assembly-CSharp 反编译核实）

- 采矿提速：Harmony Prefix 补丁 `MinerComponent.InternalUpdate(..., ref float miningSpeed, ...)`，
  把 `miningSpeed` 参数乘以配置倍率 → 产出与传送速率同步放大。
  不修改任何存档字段，卸载模组即恢复原版行为。
- 耗电增长：Harmony Postfix 补丁 `MinerComponent.SetPCState`，
  在游戏按 `speedDamper × speed² / 1e8` 设置需求能量后，按耗电倍率重设
  `PowerConsumerComponent.requiredEnergy`（保持 idle/work 比例不变）。

## 安装

1. 先安装 [BepInEx 5](https://dsp.thunderstore.io/package/xiaoye97/BepInEx/)。
2. 把 `DSPMinerMultiplier\bin\DSPMinerMultiplier.dll` 复制到
   `游戏目录\BepInEx\plugins\DSPMinerMultiplier\`。
3. 启动游戏。

## 配置

首次运行后生成 `BepInEx\config\com.byboy.dsp.minermultiplier.cfg`：

```ini
[General]

## 采矿机的采矿速度倍率，范围 1 ~ 1000。耗电按 1~256 线性同步增长。
# Setting type: Single
# Default value: 10
采矿倍率 MiningMultiplier = 10
```

装了 ConfigurationManager 插件的话可在游戏内实时调整。

## 从源码编译

```
dotnet build DSPMinerMultiplier/DSPMinerMultiplier.csproj -c Release
```

- 默认使用仓库 `libs\` 目录里的引用 DLL（已包含从游戏提取的
  `Assembly-CSharp.dll`、BepInEx 5.4.22 与 UnityEngine 2021.3.33 模块）。
- 如果配置了 csproj 里的 `<GameDir>` 指向真实游戏目录，构建后会把 dll
  自动复制到游戏的 `BepInEx\plugins\DSPMinerMultiplier\`。

> 提示：`libs\Assembly-CSharp.dll` 目标框架是 netstandard2.1（新版 DSP），
> 项目因此使用 netstandard2.1。若你的游戏是旧版（netstandard2.0），
> 把 csproj 的 `<TargetFramework>` 改回 netstandard2.0 并用游戏自带的
> Assembly-CSharp.dll 重新编译即可。
