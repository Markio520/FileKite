# 第三方组件与许可声明

本项目（纸鸢 · FileKite）自有源码基于 [Apache License 2.0](../LICENSE) 开源发布。所有随包及按需获取的第三方开源组件版本固定在 [Directory.Packages.props](../Directory.Packages.props) 与 [engines/manifest.json](../engines/manifest.json) 中，对应许可文本保存在本目录并在构建时复制到输出目录的 `Licenses/`。

## 核心运行时依赖

| 组件 | 版本 | 用途 | 许可证与声明 |
| --- | --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM 属性通知与命令生成 | MIT · [许可证](licenses/CommunityToolkit.Mvvm-8.4.2.txt) |
| Microsoft.WindowsAppSDK | 2.6.4 | WinUI 3 与 Windows App SDK | [SDK 许可](licenses/Microsoft.WindowsAppSDK-2.6.4.txt) · [NOTICE](licenses/Microsoft.WindowsAppSDK-2.6.4-NOTICE.txt) |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2957 | Windows 资源编译与打包工具 | [Windows SDK 许可](https://aka.ms/WinSDKLicenseURL) |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.7.1 | 命令行启动、调试与打包集成 | MIT · [项目源码](https://github.com/microsoft/WinAppCli) |
| Microsoft.Data.Sqlite | 10.0.15 | 自研搜索与空间分析持久化索引 | MIT · [许可证](index-lab/Microsoft.Data.Sqlite-10.0.15-LICENSE.txt) |
| SQLitePCLRaw | 2.1.13 | SQLite 原生库绑定（SQLite 本体为公有领域） | Apache-2.0 · [许可证](licenses/Apache-2.0.txt) · [说明](index-lab/SQLite-components.txt) |
| BCUninstaller（核心适配） | 6.3 | 软件卸载识别与残留清理引擎 | Apache-2.0 · [适配说明](BCUninstaller-adaptation.md) |

## 本地预览与格式转换引擎

调用架构与版本约束见 [engines/README.md](../engines/README.md)，上游对应源码获取方式见 [SOURCE-ACCESS.md](SOURCE-ACCESS.md)。

| 组件 | 版本 | 当前用途 | 许可证与声明 |
| --- | --- | --- | --- |
| Magick.NET-Q16-x64 | 15.0.0 | 图片解码、预览及多格式编码 | Apache-2.0 · [Copyright](licenses/Magick.NET-15.0.0-Copyright.txt) · [NOTICE](licenses/Magick.NET-15.0.0-NOTICE.txt) |
| PDFiumCore | 157.0.8100 | .NET PDFium 调用封装 | Apache-2.0 · [许可证](licenses/PDFiumCore-157.0.8100.txt) |
| bblanchon.PDFium.Win32 / PDFium | 157.0.8100 | PDF 渲染预览与按页导出图片 | BSD 风格及第三方声明 · [NOTICES](licenses/PDFium-157.0.8100-NOTICES.txt) · [明细目录](licenses/PDFium-157.0.8100/) |
| PdfPig | 0.1.16 | 多图合成 PDF 与文本提取 | Apache-2.0 · [许可证](licenses/PdfPig-0.1.16.txt) |
| FFmpeg（BtbN LGPL shared） | 8.1.3-9-g29e619e767 | 视频首帧预览与音视频本地转换 | LGPL-3.0 · 随 `Engines/ffmpeg/` 保留原包 LICENSE，构建配置见 [ffmpeg-build-configuration.txt](ffmpeg-build-configuration.txt) |
| LibreOffice（按需下载 x64） | 26.2.6.3 | Office / ODF 文档互转与导出 PDF | MPL-2.0 · [license.txt](licenses/LibreOffice-26.2.6-license.txt) · [NOTICE](licenses/LibreOffice-26.2.6-NOTICE.txt) |

## 其他资源与测试依赖

- **空白 DOC 模板**：取自 Apache POI `REL_5_3_0` 的 `empty.doc`（不引入 POI 运行库），保留 [Apache-2.0 LICENSE](licenses/Apache-POI-blank-template-LICENSE.txt) 与 [NOTICE](licenses/Apache-POI-blank-template-NOTICE.txt)。
- **自动化测试依赖**（仅用于 `tests/`，不随应用分发）：`Microsoft.NET.Test.Sdk` 17.14.1（MIT）、`xunit` 2.9.3 与 `xunit.runner.visualstudio` 3.1.4（Apache-2.0）。
