# 对应源码与许可获取说明

本项目（纸鸢 · FileKite）自有源码采用 [Apache License 2.0](../LICENSE) 开源许可。调用第三方程序或库不改变它们各自的许可证；原始声明保存在 `third-party/licenses/`，构建时自动复制到运行目录的 `Licenses/`；外部处理引擎各自的许可文件继续随引擎目录完整保留。

| 实际组件 | 对应源码 / 获取路径 |
| --- | --- |
| BCUninstaller 6.3 核心适配 | [固定源码提交 2e53fc13be5481029c9f7b7c2597becd1f3394fa](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller/tree/2e53fc13be5481029c9f7b7c2597becd1f3394fa)；[编译与适配说明](BCUninstaller-adaptation.md)，LICENSE/NOTICE 随运行目录分发 |
| TaskScheduler 2.12.2 | [固定源码提交 8f4803cf060b35f8299db26b45bfd6ff0f599c3c](https://github.com/dahall/TaskScheduler/tree/8f4803cf060b35f8299db26b45bfd6ff0f599c3c)；MIT 原文存于 `licenses/TaskScheduler-2.12.2-LICENSE.txt` |
| LibreOffice 26.2.6.3（按需下载） | [固定源码提交 8221e31b3ac356a1623c672912a3d2b492f7e3d1](https://github.com/LibreOffice/core/tree/8221e31b3ac356a1623c672912a3d2b492f7e3d1)；[官方版本归档](https://downloadarchive.documentfoundation.org/libreoffice/old/26.2.6.3/) 中的 `src/` 与该版第三方源码归档；组件内 `license.txt` 和 `NOTICE` 保留 |
| FFmpeg 8.1.3-9-g29e619e767 | [固定 FFmpeg 提交 29e619e767](https://github.com/FFmpeg/FFmpeg/commit/29e619e767)；[BtbN 2026-09-30 构建脚本与依赖配方](https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-09-30-13-08)，依照配方获取外部库对应版本与补丁；构建选项见 [ffmpeg-build-configuration.txt](ffmpeg-build-configuration.txt) |
| Magick.NET 15.0.0 | [固定提交 79bb8700d7fe3a564f62926ab299fc2520cb2269](https://github.com/dlemstra/Magick.NET/tree/79bb8700d7fe3a564f62926ab299fc2520cb2269)；原生 ImageMagick 及依赖声明见仓库内保存的 `NOTICE` |
| PDFiumCore 157.0.8100 | [固定源码提交 9818b101e713409e033b5489cb41c949e953dced](https://github.com/Dtronix/PDFiumCore/tree/9818b101e713409e033b5489cb41c949e953dced) |
| PDFium chromium/8100 | [固定二进制发行与构建项目](https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8100)；完整原生声明见 `licenses/PDFium-157.0.8100/` |
| PdfPig 0.1.16 | [固定源码提交 a7bb35662bbbf405efddad50aedc9bcdcf515afc](https://github.com/UglyToad/PdfPig/tree/a7bb35662bbbf405efddad50aedc9bcdcf515afc) |

除上述 BCU 源码适配（Apache-2.0）外，其余处理引擎均使用未修改的官方二进制；LibreOffice 和 FFmpeg 通过独立进程调用。LibreOffice 只在用户在「设置 → 引擎管理」点击安装或修复时下载，本地转换过程不上传任何用户文件。

若二次分发包含 FFmpeg 或相关原生库的预编译安装包，请按 LGPL-3.0 及对应第三方许可证要求一并提供相应版本源码与构建配置说明。
