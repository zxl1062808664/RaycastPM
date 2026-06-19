# RaycastPM 开发者说明

本文档面向维护和开发 RaycastPM 的开发者。普通用户说明见项目根目录的 [README](../README.md)。

## 技术栈

- 桌面框架：WPF
- 运行目标：`net10.0-windows`
- 托盘能力：Windows Forms `NotifyIcon`
- 架构方式：偏 MVVM，核心状态和命令集中在 `MainViewModel`
- 系统能力：使用 Win32 API 处理全局快捷键、前台窗口切换、窗口扩展样式和自动粘贴
- 数据存储：本地文件，默认位于 `%AppData%\RaycastPM`

## 目录结构

```text
RaycastPM/
├─ App.xaml                         WPF 全局样式和资源
├─ App.xaml.cs                      WPF 应用入口
├─ MainWindow.xaml                  主窗口 UI
├─ MainWindow.xaml.cs               主窗口行为、托盘、浮窗生命周期
├─ PlanSummaryWindow.xaml           日程简述浮窗 UI
├─ PlanSummaryWindow.xaml.cs        日程简述浮窗行为和点击穿透
├─ SystemMonitorWindow.xaml         实时监控小条 UI
├─ SystemMonitorWindow.xaml.cs      实时监控小条行为
├─ RaycastPM.Wpf.csproj             WPF 项目文件
├─ docs/
│  ├─ developer-guide.md            开发者说明
│  └─ navigation-search-rules.md    导航搜索规则用户说明
├─ Wpf/
│  ├─ Converters/                   XAML 绑定转换器
│  ├─ Models/                       应用状态、设置、数据模型
│  ├─ Services/                     搜索、剪贴板、热键、状态存储等服务
│  └─ ViewModels/                   主视图模型和命令封装
└─ README.md                        用户说明
```

## 核心文件

- `MainWindow.xaml`：定义导航、剪贴板、记事本、计划表、设置页以及计划编辑弹窗。
- `MainWindow.xaml.cs`：处理托盘菜单、窗口显示/隐藏、全局快捷键打开后的焦点恢复、计划日期控件关闭、实时监控浮窗和日程简述浮窗生命周期。
- `PlanSummaryWindow.xaml`：小型今日计划浮窗，展示当天计划列表。
- `PlanSummaryWindow.xaml.cs`：处理浮窗初始化位置、置顶、点击穿透和可拖动逻辑。
- `SystemMonitorWindow.xaml`：实时网速、CPU、GPU、内存小条。
- `Wpf/ViewModels/MainViewModel.cs`：应用的主要状态中心，包含导航搜索、剪贴板、记事本、计划表、设置、汇率、后台待机和浮窗刷新逻辑。
- `Wpf/ViewModels/PlanCalendarEventItem.cs`：日历和日程简述浮窗复用的轻量计划展示项。
- `Wpf/ViewModels/HotKeyEditorItem.cs`：设置页快捷键可视化编辑行模型。
- `Wpf/Models/AppModels.cs`：计划项、记事项、设置项和整个应用状态的数据模型。
- `Wpf/Services/StateStore.cs`：负责读写 `%AppData%\RaycastPM\state.json`，并做配置归一化。
- `Wpf/Services/LocalFileSearchService.cs`：本地文件索引和导航搜索服务。
- `Wpf/Services/SystemMonitorService.cs`：读取实时网速、CPU、GPU 和内存使用率。
- `Wpf/Services/ClipboardMonitor.cs`：监听系统剪贴板变化。
- `Wpf/Services/GlobalHotKeyService.cs`：注册和处理 Windows 全局快捷键。
- `Wpf/Services/AppDiagnostics.cs`：调试输出和文件日志。

## 模块说明

### 导航

- 支持本机应用、文件、文件夹、网页搜索、网址直开、计算和汇率换算。
- 应用缓存优先加载，完整索引在后台补齐。
- 使用次数会影响结果排序。

### 剪贴板

- 支持文本、图片、文件、链接和颜色历史。
- 双击可复制并尽量恢复到打开前窗口自动粘贴。
- 最大保留 120 条。

### 记事本

- 支持标题、正文、图片附件和剪贴板图片粘贴。
- 打开后不会自动隐藏，适合连续编辑。

### 计划表

计划表是当前最复杂的模块之一，主要由以下部分组成：

- 月历视图：左侧显示 42 格日历，日期有计划时显示提示点。
- 当日安排区：右侧按选中日期显示计划卡片。
- 计划编辑弹窗：点击卡片上的“编辑”进入，默认关闭。
- 计划字段：标题、状态、优先级、开始时间、计划完成时间、实际完成时间、说明、图片附件。

当前交互特性：

- 计划模块通过独立快捷键打开，不走顶部切换栏。
- 打开计划模块后不会因为失焦自动隐藏，需按既有逻辑主动关闭。
- 时间支持日期和具体时分，时分可下拉选择，也可手动输入。
- 图片支持从文件添加，也支持直接从剪贴板粘贴。
- 卡片区支持直接编辑和删除。

### 日程简述浮窗

日程简述浮窗由设置页控制，主要用于独立展示“今天的计划列表”。

对应设置项位于 `AppSettings`：

- `PlanSummaryWindowEnabled`
- `PlanSummaryWindowTopmost`
- `PlanSummaryWindowClickThrough`
- `PlanSummaryWindowOpacity`

实现要点：

- 浮窗由 `MainWindow.xaml.cs` 统一创建、显示、隐藏和销毁。
- 数据来自 `MainViewModel.TodayPlanSummaryItems`。
- 刷新逻辑走 `_planSummaryTimer`，默认每分钟刷新一次。
- 列表始终基于全部计划计算，不受主界面筛选条件影响。
- 点击穿透通过扩展窗口样式 `WS_EX_TRANSPARENT` 实现。
- 当未开启“不可点击”时，浮窗允许通过 `DragMove()` 拖动。

### 实时监控浮窗

- 可独立显示实时下载/上传速度、CPU、GPU、内存。
- 与主窗口分离，显示时继续后台采样。
- 隐藏后会停止采样并重置采样基线。

## 状态存储

应用状态统一保存在：

```text
%AppData%\RaycastPM\state.json
```

主要内容包括：

- 应用设置
- 导航使用次数
- 剪贴板历史
- 记事内容
- 计划表内容

`StateStore.NormalizeState(...)` 会负责做兼容和归一化，例如：

- 缺省设置补默认值
- 日志目录标准化
- 字号和透明度范围校正
- 无效剪贴板/记事/计划项清理

## 导航索引和缓存

导航索引采用两级缓存：

```text
%AppData%\RaycastPM\file-index-app-cache-v1.mpack
%AppData%\RaycastPM\file-index-cache-v6.bin
```

- `file-index-app-cache-v1.mpack`：应用搜索的快速启动缓存
- `file-index-cache-v6.bin`：完整文件/文件夹 packed 索引缓存
- 启动时优先加载应用缓存，完整索引后台补齐
- 手动重建时显示进度
- 增量变化通过 `FileSystemWatcher` 监听

## 后台待机策略

`MainWindow.xaml.cs` 会把主窗口、实时监控浮窗和日程简述浮窗的状态同步到 `MainViewModel`。

主窗口隐藏后会暂停：

- 索引进度 UI 刷新
- 导航搜索防抖和搜索任务
- 汇率定时刷新

保持运行：

- 全局快捷键
- 托盘入口
- 剪贴板监听
- 文件系统增量监听
- 已显示的实时监控浮窗采样
- 已显示的日程简述浮窗计划刷新

## 构建方式

需要安装支持 .NET 10 的 SDK。

常规构建：

```powershell
dotnet build .\RaycastPM.Wpf.csproj
```

运行：

```powershell
dotnet run --project .\RaycastPM.Wpf.csproj
```

如果默认 `bin` 输出目录被正在运行的程序占用，也可以输出到临时目录：

```powershell
dotnet build .\RaycastPM.Wpf.csproj -o .\.tmp\tray-build
```

## 维护建议

- 修改计划模块时，优先检查 `MainWindow.xaml`、`MainViewModel.cs` 和 `AppModels.cs` 三处是否同步。
- 修改浮窗行为时，注意同步检查 `MainWindow.xaml.cs` 与独立浮窗 `.xaml.cs`。
- 修改设置项时，同时更新：
  - `AppSettings`
  - `StateStore.NormalizeState`
  - `MainViewModel` 属性
  - 设置页 XAML
- 涉及计划时间控件时，注意验证：
  - 日期可见
  - 时间可手输
  - 弹窗编辑可保存
  - 日程简述浮窗同步刷新
