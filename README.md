# RaycastPM 项目总结

RaycastPM 是一个 Windows 桌面效率工具，当前主版本基于 .NET 10 + WPF 开发。应用常驻系统托盘，通过快捷键或托盘菜单打开导航、剪贴板、记事本和设置等功能，定位接近一个轻量级的 Raycast/PowerToys Run 工具。

## 当前技术栈

- 桌面框架：WPF
- 运行目标：`net10.0-windows`
- 托盘能力：Windows Forms `NotifyIcon`
- 架构方式：偏 MVVM，核心状态和命令集中在 `MainViewModel`
- 系统能力：使用 Win32 API 处理全局快捷键、前台窗口切换、剪贴板图像读取和自动粘贴
- 数据存储：本地 JSON 文件，默认位于 `%AppData%\RaycastPM`

## 主要功能

### 1. 托盘常驻

应用启动后常驻系统托盘。托盘右键菜单包含：

- 退出
- 导航
- 剪贴板
- 记事本
- 设置

除记事本外，其他界面支持点击窗口外或按 `Esc` 自动隐藏。记事本保留独立关闭按钮，便于持续编辑。

### 2. 导航搜索

导航栏用于搜索和打开本机应用、文件、文件夹，也支持计算和汇率换算。

核心规则：

- 应用优先展示
- 文件优先于文件夹
- 文件/文件夹按名称匹配，而不是优先匹配路径
- 使用次数越多，排序越靠前
- 支持类似 Everything 的组合搜索规则
- 启动时优先加载轻量应用缓存，应用可用后立即进入可搜索状态
- 完整文件/文件夹缓存使用低内存 packed 二进制格式，并在后台异步加载，不阻塞主线程，也不额外显示加载提示
- 首次缺少完整 packed 缓存时会在后台静默重建；手动重建时显示索引进度
- 增量变化通过 `FileSystemWatcher` 监听，旧 NTFS FRN/MFT 缓存会被清理，不再加载到内存
- 设置页提供手动重新扫描硬盘数据按钮
- 设置页提供清除导航缓存数据按钮，删除前需要二次确认
- 首次连应用缓存也不存在或手动重建时显示索引进度

用户搜索规则说明见：[`docs/navigation-search-rules.md`](docs/navigation-search-rules.md)。

文件索引缓存保存在：

```text
%AppData%\RaycastPM\file-index-app-cache-v1.mpack
%AppData%\RaycastPM\file-index-cache-v6.bin
```

其中 `file-index-app-cache-v1.mpack` 是启动快路径使用的应用缓存，`file-index-cache-v6.bin` 是完整文件/文件夹 packed 索引缓存。旧版本的 `file-index-cache-v*.mpack` / `file-index-cache.json` / `file-index-cache.bin` / `file-index-ntfs-journal.mpack` 不再读取，启动或保存缓存时会自动清理。

### 3. 剪贴板

剪贴板模块会记录文本、图片、文件、链接和颜色历史，支持搜索、分类筛选、删除和复制。

当前交互：

- 双击剪贴板历史项会复制该内容
- 复制后自动隐藏剪贴板窗口
- 自动切回打开剪贴板前的窗口
- 自动发送 `Ctrl+V` 完成粘贴
- 文件类记录复制回去时会尽量还原为系统文件剪贴板

剪贴板历史数量目前限制为最多 120 条。

### 4. 记事本

记事本模块用于快速记录内容，支持：

- 新建记事
- 删除记事
- 记事搜索
- 历史记事列表弹窗
- 图片附件，支持从剪贴板粘贴或从文件添加
- 手动输入字号
- 字号滑块调整
- 标题和正文自动保存

记事数据保存在本地状态文件中。

### 5. 设置

设置页包含通用设置和使用统计。

当前能力：

- 查看导航项使用次数
- 清空使用统计
- 重新扫描硬盘索引
- 清除导航缓存数据，执行前需要二次确认
- 开启或关闭 Windows 开机自启
- 开启或关闭日志输出，并选择日志输出目录
- 修改记事本字号
- 查看数据目录
- 可视化编辑导航、剪贴板、记事本和设置的全局快捷键

## 快捷键

默认快捷键定义在 `Wpf/Models/AppModels.cs`，也可以在设置页用修饰键按钮和主键下拉框直接编辑：

```text
导航：Ctrl + Alt + Space
剪贴板：Ctrl + Alt + V
记事本：Ctrl + Alt + N
设置：Ctrl + Alt + S
```

设置页的快捷键修改会立即保存，并重新注册全局快捷键；每一行的 `↻` 按钮会恢复该功能的默认组合。

## 目录结构

```text
RaycastPM/
├─ App.xaml                         WPF 全局样式和资源
├─ App.xaml.cs                      WPF 应用入口
├─ MainWindow.xaml                  主窗口 UI
├─ MainWindow.xaml.cs               窗口行为、托盘、隐藏、自动粘贴
├─ RaycastPM.Wpf.csproj             WPF 项目文件
├─ docs/
│  └─ navigation-search-rules.md     导航搜索规则用户说明
├─ Wpf/
│  ├─ Converters/                   XAML 绑定转换器
│  ├─ Models/                       应用状态、设置、数据模型
│  ├─ Services/                     搜索、剪贴板、热键、状态存储等服务
│  └─ ViewModels/                   主视图模型和命令封装
└─ README.md                        项目说明
```

## 核心文件说明

- `MainWindow.xaml`：定义主界面布局，包括导航、剪贴板、记事本、设置等区域。
- `MainWindow.xaml.cs`：处理托盘菜单、窗口显示隐藏、全局快捷键打开后的焦点恢复、剪贴板双击自动粘贴。
- `Wpf/ViewModels/MainViewModel.cs`：应用的主要状态中心，包含搜索、剪贴板、记事本、设置、汇率、使用次数等逻辑。
- `Wpf/ViewModels/HotKeyEditorItem.cs`：设置页快捷键可视化编辑行的状态模型。
- `Wpf/Services/LocalFileSearchService.cs`：本地文件索引和搜索服务，负责缓存优先启动、增量监听、全量重建、查询解析和结果排序。
- `Wpf/Services/ClipboardMonitor.cs`：监听系统剪贴板变化，支持文本、图片、文件、链接和颜色内容。
- `Wpf/Services/ClipboardClassifier.cs`：负责剪贴板文本内容的分类识别。
- `Wpf/Services/AppDiagnostics.cs`：负责控制台、调试输出和可配置文件日志。
- `Wpf/Services/GlobalHotKeyService.cs`：注册和处理 Windows 全局快捷键。
- `Wpf/Services/StateStore.cs`：负责读取和保存 `%AppData%\RaycastPM\state.json`。
- `Wpf/Services/CurrencyConverter.cs`：处理汇率查询和 10 分钟刷新逻辑。

## 本地数据

应用本地数据目录：

```text
%AppData%\RaycastPM
```

主要文件：

```text
state.json                         应用设置、剪贴板历史、记事内容、使用次数
file-index-app-cache-v1.mpack      导航启动快路径使用的应用索引缓存
file-index-cache-v6.bin            本机文件/文件夹 packed 完整索引缓存
logs\raycastpm-yyyyMMdd.log        可配置开启的运行日志
```

## 构建方式

需要安装支持 .NET 10 的 SDK。

常规构建：

```powershell
dotnet build .\RaycastPM.Wpf.csproj
```

如果默认 `bin` 输出目录被正在运行的应用占用，可以输出到临时目录：

```powershell
dotnet build .\RaycastPM.Wpf.csproj -o .\.tmp\tray-build
```

运行：

```powershell
dotnet run --project .\RaycastPM.Wpf.csproj
```

## 当前清理状态

项目已经切换为 WPF/Windows 主版本，Swift/macOS 版本遗留内容已清理，包括：

- `Package.swift`
- `Sources/`
- `dist/`
- `.build/`
- `.swiftpm-cache/`
- `.clang-module-cache/`
- `.home/`

当前仓库应主要围绕 WPF 项目继续维护。

## 后续可优化方向

- 为文件索引增加更明确的排除目录配置
- 为剪贴板历史增加固定/收藏功能
- 为导航搜索、剪贴板和记事本补基础单元测试
