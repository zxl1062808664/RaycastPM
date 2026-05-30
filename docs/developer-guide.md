# RaycastPM 开发者说明

本文档面向维护和开发 RaycastPM 的开发者。普通用户说明见项目根目录的 [README](../README.md)。

## 技术栈

- 桌面框架：WPF
- 运行目标：`net10.0-windows`
- 托盘能力：Windows Forms `NotifyIcon`
- 架构方式：偏 MVVM，核心状态和命令集中在 `MainViewModel`
- 系统能力：使用 Win32 API 处理全局快捷键、前台窗口切换、剪贴板图像读取和自动粘贴
- 数据存储：本地文件，默认位于 `%AppData%\RaycastPM`

## 目录结构

```text
RaycastPM/
├─ App.xaml                         WPF 全局样式和资源
├─ App.xaml.cs                      WPF 应用入口
├─ MainWindow.xaml                  主窗口 UI
├─ MainWindow.xaml.cs               窗口行为、托盘、隐藏、自动粘贴
├─ RaycastPM.Wpf.csproj             WPF 项目文件
├─ SystemMonitorWindow.xaml         实时监控小条 UI
├─ docs/
│  ├─ developer-guide.md             开发者说明
│  └─ navigation-search-rules.md     导航搜索规则用户说明
├─ Wpf/
│  ├─ Converters/                   XAML 绑定转换器
│  ├─ Models/                       应用状态、设置、数据模型
│  ├─ Services/                     搜索、剪贴板、热键、状态存储等服务
│  └─ ViewModels/                   主视图模型和命令封装
└─ README.md                        默认用户说明
```

## 核心文件

- `MainWindow.xaml`：定义主界面布局，包括导航、剪贴板、记事本、设置等区域。
- `MainWindow.xaml.cs`：处理托盘菜单、窗口显示隐藏、全局快捷键打开后的焦点恢复、剪贴板双击自动粘贴、UI 活跃状态同步。
- `SystemMonitorWindow.xaml`：实时网速、CPU、GPU、内存小条。
- `Wpf/ViewModels/MainViewModel.cs`：应用的主要状态中心，包含搜索、剪贴板、记事本、设置、汇率、使用次数、后台待机策略等逻辑。
- `Wpf/ViewModels/HotKeyEditorItem.cs`：设置页快捷键可视化编辑行的状态模型。
- `Wpf/Services/LocalFileSearchService.cs`：本地文件索引和搜索服务，负责缓存优先启动、增量监听、全量重建、查询解析和结果排序。
- `Wpf/Services/LocalFileSearchService.Ntfs.cs`：保留 NTFS 相关辅助代码；当前低内存索引策略不再加载旧 NTFS FRN 缓存。
- `Wpf/Services/SystemMonitorService.cs`：读取实时网速、CPU、GPU 和内存使用率。
- `Wpf/Services/ClipboardMonitor.cs`：监听系统剪贴板变化，支持文本、图片、文件、链接和颜色内容。
- `Wpf/Services/ClipboardClassifier.cs`：负责剪贴板文本内容的分类识别。
- `Wpf/Services/AppDiagnostics.cs`：负责控制台、调试输出和可配置文件日志。
- `Wpf/Services/GlobalHotKeyService.cs`：注册和处理 Windows 全局快捷键。
- `Wpf/Services/StateStore.cs`：负责读取和保存 `%AppData%\RaycastPM\state.json`。
- `Wpf/Services/CurrencyConverter.cs`：处理汇率查询和 10 分钟刷新逻辑。

## 导航索引和缓存

导航索引采用两级缓存：

```text
%AppData%\RaycastPM\file-index-app-cache-v1.mpack
%AppData%\RaycastPM\file-index-cache-v6.bin
```

- `file-index-app-cache-v1.mpack` 是启动快路径使用的应用索引缓存。
- `file-index-cache-v6.bin` 是完整文件/文件夹 packed 二进制索引缓存。
- 启动时先加载应用缓存，应用搜索可立即使用。
- 完整 packed 缓存在后台异步加载，不阻塞主线程，也不额外显示加载提示。
- 首次缺少完整 packed 缓存时会后台静默重建；手动重建时显示索引进度。
- 旧版本的 `file-index-cache-v*.mpack`、`file-index-cache.json`、`file-index-cache.bin`、`file-index-ntfs-journal.mpack` 不再读取，启动或保存缓存时会自动清理。
- 增量变化通过 `FileSystemWatcher` 监听，避免长期维护大体积 NTFS FRN/MFT 内存缓存。

低内存要点：

- 完整索引保存为紧凑 packed 二进制，而不是反序列化为大量对象。
- packed 候选索引用 delta-varint 压缩。
- 首次重建时使用收紧的大数组扩容策略，并在大数组扩容、磁盘根扫描完成后做受控 LOH 回收。
- 应用缓存加载完成后，后台读取或重建完整缓存不会重置前台进度，也不会阻塞用户搜索应用。

## 后台待机策略

`MainWindow.xaml.cs` 会把主窗口和实时监控小条的显示状态同步到 `MainViewModel.SetUiActivityState`。

主窗口隐藏后暂停：

- 索引进度 UI 刷新
- 导航搜索防抖和正在运行的搜索任务
- 汇率定时刷新

保持运行：

- 全局快捷键
- 托盘入口
- 剪贴板历史监听
- 文件系统增量监听

实时监控小条可见时继续 1 秒采样；小条隐藏或关闭设置后停止 CPU/GPU/内存/网速采样，并重置采样基线，避免恢复时网速被后台时间摊薄。

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
