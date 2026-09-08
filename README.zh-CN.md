# Path of Idle 游戏内搜索

[English](README.md) · [한국어](README.ko.md) · **简体中文** · [繁體中文](README.zh-TW.md)

非官方 Windows 模组，在游戏内搜索物品、搭配装备与技能、批量开箱、管理仓库和强化。不需要浏览器仪表盘或独立服务器。

**[下载最新版](https://github.com/Hangeulkim/PathOfIdleInGameSearch/releases/latest)** · [完整指南与示例（英文）](GUIDE.md)

独立仓库为 **Hangeulkim/PathOfIdleInGameSearch**。**1.1.9** 包含 Healer 等拥有跨职业 Alien 技能角色的自动装备修复。以前的 `path-of-idle-stats` 链接是发布错误，请使用本仓库。

## 安装与更新

**1.1.10 修复 Windows 安装器启动错误**（`all.ps1` / `is not recognized`）。请重新下载 ZIP 并完整解压到新文件夹。本次未更改自动搭配算法，下方 v1.1.9 截图中的界面仍相同。

1. 下载并完整解压 `PathOfIdleInGameSearch-v1.1.10.zip`。
2. 关闭游戏，运行 `install.bat`。
3. 启动游戏，按 **F3** 或 **Ctrl+F**。

安装程序验证 SHA-256 并保留现有设置。仅当缺少 BepInEx 时才验证并安装 ZIP 内附的固定官方 BepInEx 6 IL2CPP x64 原版，无需在安装过程中另行下载。包内同时提供对应源码和许可证，不包含游戏文件。已有 BepInEx 的用户也可将单独下载的 DLL 放入 `BepInEx/plugins`。

## 功能与示例

- 按名称、品质、部位、等级、词缀或套装搜索背包、普通仓库及 Vault。查看完整描述并按游戏规则移动物品。`critical damage -health` 要求前两个词并排除 health；`|` 表示或，引号表示短语。
- Auto Build 同时比较八个装备栏、技能和点数；可选技能转换消耗正常游戏资源。已学 Alien 技能和装备赋予技能分开处理。
- 主题：Auto、Physical、Elemental、Fire、Ice、Lightning、Minion、Bleed、Corrosion、Critical、Support、Defense。优先度 0 偏向通用表现，100 偏向所选主题，不强制指定物品。
- 火焰流选 Fire 并提高优先度；召唤流选 Minion，按原生标签、归属、攻击类型及继承属性判断 Ally、Sentry、Totem；生存流选 Defense，在相同攻击下比较减伤、闪避、护盾、恢复和存活时间。
- Ignore forge bonuses 仅在比较时采用强化前核心数值，真实强化、词缀、符文和套装效果保留。
- 批量开装备/符文箱；Keep Vault gear, sell rest 保留进入 Vault 的奖励，立即出售确认的溢出物品。更好奖励替换旧物品时，被挤出的旧物品才出售。
- 普通仓库整体出售单独确认，排除 Vault、背包及已装备物品。
- Forge 比较下一级收益或最大强化已装备物品。神话强化批量检查合格的未装备传说物品，不需选择角色，每次检查容量、资源并报告成功、失败与跳过数。
- 面板可拖动/缩放，支持四种语言、0.1×–100× 输入与按需显示日志。后台启动和全局快捷键默认关闭。

## 对照画面操作

以下为 v1.1.9 实际游戏截图，保存的角色、数量、速度与选项只是示例，不是推荐默认值。点击图片可放大。

### 搜索已有物品

在 **Item Search** 输入文字，选择是否包含 Warehouse/Vault，再用结果上方的两个标签切换位置。搜索本身不会出售或装备物品。

![v1.1.9 搜索框和仓库结果](docs/images/v1.1.9-search.jpg)

*顶部是 `minion` 文字查询，不是 Auto Build 主题。中间按钮筛选品质；每项右侧的 **Move to inventory** 只移动该物品。悬停可查看完整效果。*

### 选择自动搭配目标并确认

先核对蓝框里的角色，再选择主题和优先度。图中是 Defense；100% 和不限时是此存档的选择，**Default 60** 可恢复默认搜索时间。

![v1.1.9 角色、主题与优先度](docs/images/v1.1.9-auto-build-theme.jpg)

向下滚动，检查仓库范围、**Ignore forge bonuses** 和付费技能转换选项，然后使用 **Auto-build Gear + Skills**。第一次点击准备，5 秒内再次确认才开始计算并可能应用；后面没有额外批准画面。

![v1.1.9 自动搭配选项和执行按钮](docs/images/v1.1.9-auto-build.jpg)

*宽按钮是装备与技能一起处理；下面两个 Advanced 按钮只处理其中一侧。截图时没有执行自动搭配。*

### 开箱与仓库出售

在目的地行选择 **Inventory / Warehouse / Vault + Sell**，下方选择箱子品质，再用显示数量的装备或符文开箱按钮。

![v1.1.9 开箱目的地和出售按钮](docs/images/v1.1.9-bulk-open.jpg)

***Vault + Sell** 保留进入 Vault 的物品并出售确认的溢出。红色 **Sell all Warehouse gear (Vault excluded)** 是单独清理普通仓库，不是开箱按钮。已完成的出售不能撤销。*

### Forge 与神话强化

Forge 上方两个按钮分别用于升一级和最大强化已装备物品；下方紫色按钮先检查未装备传说的神话候选。

![v1.1.9 Forge 和 Mythic 检查](docs/images/v1.1.9-forge.jpg)

***Check all Mythic candidates (no consumption)** 第一次仅检查，12 秒内确认同一批候选才进行真实付费尝试；失败会销毁传说物品，且不需要选择角色。*

## 计算与安全

自动化由你选择角色、设置主题并确认后启动，在游戏内本地读取数据和调用正常游戏函数，不是需要 AI 订阅或 API 密钥的云端代理。主按钮第一次点击准备，5 秒内再次点击才开始计算及实际应用；计算完成后没有额外的批准画面。技能转换默认开启、默认最多 12 次正常付费尝试，可在运行前关闭。取消只在计算阶段可用。

先按兼容性、等级和效果结构缩小候选，再比较完整装备与技能计划；不是仅凭稀有度或物品名称装备。主题 100 不代表强制每个技能都是该元素。30% 效果若有 10 次机会，在计算中计为期望 3 次，不操纵真实战斗概率。失败时尝试恢复装备、技能及本次花费，但从 Vault 取出的候选可能保留在普通仓库。

**旧装备注意：** Auto Build 成功后的整理会尝试将替换下来的物品存入 Vault，未保留在其中的可售物品可能被出售；不是保留所有旧装备的模式。完整步骤见[英文说明](README.md#auto-build-from-your-click-to-the-equipped-result)。

搜索时间 10 秒至不限，默认 60 秒；候选比较的模拟战斗固定为 30 秒，应用及验证可能额外耗时。包含支持的技能时序、生命/魔力消耗、恢复、召唤、实际 2/4 件套、现有符文/符文之语；概率效果使用期望触发次数，不自动重排散装符文。

这不是所有敌人下的精确 DPS，也不保证全局最优，新机制可能需要更新。应用失败会尝试恢复并验证原状态。[测试说明](scripts/HEALER-REGRESSION.md)区分当前存档检查与历史精确结果；不代表所有未知存档已验证。

**自动化前请备份存档。出售和神话强化失败造成的销毁不可撤销。** 卸载时关闭游戏并运行 `uninstall.bat`，保留设置及其他模组。非官方粉丝项目，与游戏开发者及 BepInEx 无关联。
