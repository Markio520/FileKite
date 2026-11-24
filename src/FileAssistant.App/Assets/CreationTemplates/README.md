# 空白创建模板

- `blank.docx`：项目生成的最小空白 WordprocessingML 包，包含空段落及 A4 页设置，无外链、宏、个人元数据。
- `blank.doc`：Apache POI `REL_5_3_0` 的空白 Word 97 测试文档，保持源文件字节；SHA-256 `5F421A3970C70F12296073478380C0849A54C03FB2F4842392C7D3C5039BF1EB`。
  来源：https://github.com/apache/poi/blob/REL_5_3_0/test-data/document/empty.doc
  Apache-2.0 许可和 NOTICE 见 `third-party/licenses/Apache-POI-blank-template-*`。原 DOC 样本包含历史文档元数据，不来自用户文件。

仅随应用分发这些小型模板；运行时按经过身份和哈希检查的模板复制，不联网、不启动 Office、不改变模板原件。其他扩展名由用户提供对应模板，或明确选择零字节占位文件。
