# 可拖动悬浮窗（floating.1）

本版本基于 v2.6.0，保留 `76aafdc` 的多尺寸应用图标修复。

- 按住数字条的任意位置，用鼠标左键拖动；松开后保存位置，重启恢复。
- 轻点仍打开或关闭额度详情，右键仍打开菜单。拖动不会误开详情。
- 首次使用显示在主显示器工作区右下角，避开任务栏。原有外观、额度、缩放等设置继续保留。
- 显示器移除、分辨率或 DPI 改变时，窗口会限制在当前可用显示器的工作区内。跨屏拖动期间允许越过边界，松开后再校正。
- 右键托盘图标或悬浮窗，选择“重置悬浮窗位置”，可恢复到主显示器右下角并重新显示窗口。
- 隐藏、显示、额度刷新及每秒置顶检查不会把窗口吸回任务栏。

位置与原设置一起保存在 `%LOCALAPPDATA%\CodexMeter\settings.ini`，新增字段为 `WidgetPositionSaved`、`WidgetX`、`WidgetY`。坐标是虚拟桌面的物理像素，支持负坐标。恢复默认设置也会重置位置。

先退出旧实例，再运行压缩包内的 `CodexQuotaTray-v2.6.0-floating.1.exe`。此文件是未签名的社区修改版，尚未发布为 GitHub Release。原有手动检查更新功能仍指向 SYD-Official 上游版本。

## 构建与验证

Windows PowerShell 下运行 `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-floating.ps1`。使用 Windows 自带的 .NET Framework C# 编译器，无需安装 .NET SDK。

脚本编译完整应用，保留仓库内的 ICO，并运行 `tests/WidgetTests.cs`。测试仅使用人工数据和 `build/floating/fixtures` 下的临时设置；测试程序不包含主程序入口、会话读取器或完成通知监视器。不会运行主程序、上游 Release EXE、原 ParserTests 或 PreviewHarness，不读取 Codex 凭据或真实会话。

测试覆盖位置持久化、旧设置兼容、负坐标、缺失显示器、工作区边缘、DPI 消息、拖动阈值、点击抑制、鼠标捕获丢失、重复布局和隐藏状态。压缩包包含测试结果、源码哈希及构建信息。

多屏布局和 DPI 转换采用人工几何数据及窗口消息验证。真实鼠标跨不同 DPI 屏幕、显示器热插拔和实际详情弹窗操作仍需人工验收。
