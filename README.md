# dspKj - 戴森球计划模组集合

个人开发的戴森球计划（Dyson Sphere Program）BepInEx 模组集合。

## 模组列表

### DSPBlueprintTools 蓝图建筑工具

蓝图建筑工具模组，坐标/高度/缩放精确到小数点后 4 位，默认快捷键 `ALT+4`（可在配置文件修改）。

- **坐标偏移**：按偏移量与数量批量偏移蓝图坐标（头尾端点同步）
- **垂直叠加**：沿 Z 轴（高度方向）按层高叠加多层
- **线性变换**：三轴缩放、绕竖直轴旋转、批量替换建筑型号
- **传送带合并最长化（省带子）**：把逐格传送带按几何邻接串链后稀疏化，上限按游戏真实规则（端点世界距离 ≤ √5.3 ≈ 2.3022 米）结合纬度/高度精确计算，并参考蓝图内已探测的最长段
- **剪贴板读取**：直接读取游戏内剪贴板蓝图；蓝图详情窗口旁有"读取"按钮（原生 UI 注入，零每帧开销）

详见 [DSPBlueprintTools/README.md](DSPBlueprintTools/README.md)。

### DSPMinerMultiplier 矿机倍率

详见 [DSPMinerMultiplier/README.md](DSPMinerMultiplier/README.md)。

## 构建

```powershell
dotnet build DSPBlueprintTools/DSPBlueprintTools.csproj -c Release
dotnet build DSPMinerMultiplier/DSPMinerMultiplier.csproj -c Release
```

引用优先使用仓库 `libs/` 目录；如需针对本机游戏版本编译，修改 `.csproj` 中的 `GameDir`。

## 安装

通过 r2modman / Thunderstore Mod Manager 搜索安装，或手动将 DLL 放入 `BepInEx/plugins/`。

- Thunderstore: <https://thunderstore.io/c/dyson-sphere-program/p/Byboy/DSPBlueprintTools/>
