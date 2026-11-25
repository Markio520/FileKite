# BCUninstaller 核心适配

版本：6.3；上游提交：`2e53fc13be5481029c9f7b7c2597becd1f3394fa`。

来源为 [BCUninstaller/Bulk-Crap-Uninstaller](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/tree/2e53fc13be5481029c9f7b7c2597becd1f3394fa)，Apache-2.0。保留上游 [LICENSE](licenses/BCUninstaller-6.3-LICENSE.txt) 和 [NOTICE](licenses/BCUninstaller-6.3-NOTICE.txt)。本文件是本项目的适配说明，不替代上游声明。

`scripts/restore-bcu.ps1` 下载固定提交源码归档，验证 SHA-256：`057B5F6727DEC7E5F0CECEE88BC9EC1ABC235A39758679DB041A69B6FEFC3A77`，解压到忽略提交的 `artifacts/dependencies/bcu/6.3/source/`。构建源文件由 `src/FileAssistant.BcuCore/FileAssistant.BcuCore.csproj` 显式选择，不修改缓存内的上游源码。

适配变化：

- 将 KlocTools、UninstallTools 和 HelperTools 的所需源码合并编译为 `FileAssistant.BcuCore.dll`，目标改为 .NET 10 Windows，保留上游资源命名。
- 排除 BCU 主程序、UninstallTools 的 Controls/Dialogs、依赖 ObjectListView 的 ColumnSorter，以及需要 COM 构建步骤的 FastSizeGenerator。不引入 ObjectListView。
- 清理辅助进程按需引用 System.Drawing.Common、System.Resources.Extensions、System.Diagnostics.PerformanceCounter、System.Management 和 TaskScheduler。构建时在项目的中间目录生成 WindowsTools 与 BooleanTools 的副本，省去仅供窗口闪烁、复选框状态使用的重载。其他工具方法沿用固定版本的上游实现。扫描器报告的可恢复错误通过原有管道返回工作台。
- 通过独立 `FileAssistant.CleanupHost` 进程调用注册表软件发现、正常卸载及 IJunkCreator 残留识别，不打开 BCU 主界面，不调用会弹出旧错误框的 JunkManager，也不调用其候选项的 Delete 方法。
- 文件/注册表清理、占用进程识别、IPC、备份与身份复查由本项目实现。只接受明确的操作类型；软件卸载重新读取登记并校验，不运行请求中直接传入的命令。
- 正常 MSI 卸载使用已核实产品 GUID 与系统 msiexec `/x /norestart`；其他软件保留自身卸载向导。保护项不能操作，退出码和登记仍存在的情况明确反馈。
- 因 BCU 的扫描器及资源访问使用反射，本阶段关闭应用 Release 裁剪，保留依赖图；正式分发时再做专门体积与签名验收。

TaskScheduler 的对应源码为 [固定提交](https://github.com/dahall/TaskScheduler/tree/8f4803cf060b35f8299db26b45bfd6ff0f599c3c)，[MIT 原文](https://github.com/dahall/TaskScheduler/blob/8f4803cf060b35f8299db26b45bfd6ff0f599c3c/license.md) 已保存到 `licenses/TaskScheduler-2.12.2-LICENSE.txt`。System.Management 及运行时相关许可/notice 保存于同目录。

软件列表当前接入桌面软件登记（HKLM/HKCU、32/64 位视图），没有接入 BCU 的所有独立功能：商店应用、Steam 等独立来源、批量静默卸载、启动项/服务处理及驱动解锁均不属于当前实现。残留匹配为候选，用户逐项核对，未默认勾选。
