using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileAssistant.Core.Features.Cleanup;
using Microsoft.Win32;

namespace FileAssistant.Infrastructure.Cleanup;

internal static class RegistryCleanup
{
    internal static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    public static string Fingerprint(ResidueKind kind, string path, string? value, CancellationToken token) => kind is ResidueKind.File or ResidueKind.Directory
        ? Hash(FileCleanupEngine.Inspect(CleanupPathPolicy.Check(path), token)) : Hash(ReadRegistry(path, kind == ResidueKind.RegistryValue ? value : null, kind == ResidueKind.RegistryValue));

    public static CleanupResponse Remove(CleanupResidue[] selected, CleanupMethod method, IProgress<CleanupProgress>? progress, CancellationToken token,
        bool backupRegistry = true, string? backupDirectory = null)
    {
        if (selected.Length > 500) throw new IOException("一次最多清理 500 个残留项目。");
        var results = new List<CleanupResult>();
        foreach (var item in selected.DistinctBy(item => item.Id))
        {
            if (token.IsCancellationRequested) { results.Add(new(item.Path, CleanupStatus.Cancelled, "未执行。", [], item.Id)); continue; }
            progress?.Report(new("正在清理选中残留", results.Count, selected.Length, item.Path));
            try
            {
                if (string.IsNullOrWhiteSpace(item.SoftwareId) || !Enum.IsDefined(item.Kind)) throw new IOException("残留清单无效。");
                if (Fingerprint(item.Kind, item.Path, item.ValueName, token) != item.Fingerprint)
                    throw new IOException("此残留已变化，请重新扫描后核对。");
                if (item.Kind is ResidueKind.File or ResidueKind.Directory)
                {
                    var root = FileCleanupEngine.Inspect(item.Path, token);
                    if (Hash(root) != item.Fingerprint) throw new IOException("此目录已变化，请重新扫描。");
                    results.AddRange(new FileCleanupEngine().Run(new([root], [], DateTimeOffset.UtcNow), method, null, token).Select(result => result with { ItemId = item.Id }));
                    continue;
                }
                var state = ReadRegistry(item.Path, item.ValueName, item.Kind == ResidueKind.RegistryValue);
                if (Hash(state) != item.Fingerprint) throw new IOException("注册项已变化，未清理。");
                string? backupFile = null;
                if (backupRegistry)
                {
                    var backup = Path.Combine(backupDirectory ?? Path.Combine(FileAssistant.Core.AppProfile.Directory, "cleanup-backups"), DateTime.UtcNow.ToString("yyyyMMdd"));
                    Directory.CreateDirectory(backup);
                    backupFile = Path.Combine(backup, Guid.NewGuid().ToString("N") + ".reg");
                    var temporary = backupFile + ".tmp";
                    try
                    {
                        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            using var writer = new StreamWriter(stream, Encoding.Unicode, leaveOpen: true);
                            writer.Write(Export(state));
                            writer.Flush();
                            stream.Flush(flushToDisk: true);
                        }
                        token.ThrowIfCancellationRequested();
                        File.Move(temporary, backupFile);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                token.ThrowIfCancellationRequested();
                if (Fingerprint(item.Kind, item.Path, item.ValueName, token) != item.Fingerprint) throw new IOException("执行前注册项发生变化，未清理。");
                var (hive, subkey) = Parse(item.Path);
                using var rootKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                if (item.Kind == ResidueKind.RegistryValue)
                {
                    using var key = rootKey.OpenSubKey(subkey, true) ?? throw new IOException("注册项已不存在。");
                    key.DeleteValue(item.ValueName ?? "", true);
                    if (key.GetValueNames().Contains(item.ValueName ?? "", StringComparer.OrdinalIgnoreCase)) throw new IOException("Windows 未确认注册值已移除。");
                }
                else
                {
                    rootKey.DeleteSubKeyTree(subkey, true);
                    using var remaining = rootKey.OpenSubKey(subkey);
                    if (remaining is not null) throw new IOException("Windows 未确认注册项已移除。");
                }
                results.Add(new(item.Path, CleanupStatus.Succeeded, backupFile is null ? "注册项已移除。" : "注册项已移除，备份：" + backupFile, [], item.Id));
            }
            catch (OperationCanceledException) { results.Add(new(item.Path, CleanupStatus.Cancelled, "未执行。", [], item.Id)); }
            catch (Exception ex) { results.Add(new(item.Path, CleanupStatus.Failed, FileCleanupEngine.Describe(ex), [], item.Id)); }
        }
        return new() { Results = results.ToArray() };
    }

    private sealed record RegistryValue(string Name, RegistryValueKind Kind, string Data);
    private sealed record RegistryNode(string Path, RegistryValue[] Values);

    private static (RegistryHive Hive, string Subkey) Parse(string path)
    {
        var parts = path.Split('\\');
        if (parts.Length < 4 || parts.Any(part => part.Length == 0 || part.IndexOfAny(['\r', '\n', '[', ']']) >= 0) || !parts[1].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase))
            throw new IOException("该注册表范围过大或不支持清理。");
        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            _ => throw new IOException("只支持当前用户和本机软件注册项。")
        };
        var subkey = string.Join('\\', parts.Skip(1));
        var effective = subkey.Replace(@"SOFTWARE\Wow6432Node\", @"SOFTWARE\", StringComparison.OrdinalIgnoreCase);
        var protectedRoots = new[] { @"SOFTWARE\Classes", @"SOFTWARE\Classes\CLSID", @"SOFTWARE\Classes\Interface", @"SOFTWARE\Classes\TypeLib",
            @"SOFTWARE\Microsoft", @"SOFTWARE\Microsoft\Windows", @"SOFTWARE\Microsoft\Windows\CurrentVersion",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows NT", @"SOFTWARE\Policies" };
        if (effective.Split('\\').Length < 3 || protectedRoots.Contains(effective, StringComparer.OrdinalIgnoreCase) ||
            effective.StartsWith(@"SOFTWARE\Policies\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能移除共享的 Windows 注册表目录。");
        return (hive, subkey);
    }

    private static RegistryNode[] ReadRegistry(string path, string? valueName, bool singleValue)
    {
        var (hive, subkey) = Parse(path);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var selected = root.OpenSubKey(subkey) ?? throw new IOException("注册项已不存在。");
        var nodes = new List<RegistryNode>();
        var budget = 10_000;
        void Read(RegistryKey key, int depth)
        {
            if (depth > 64 || --budget < 0) throw new IOException("注册项过大，请缩小处理范围。");
            var names = singleValue ? new[] { valueName ?? "" } : key.GetValueNames().Order(StringComparer.Ordinal).ToArray();
            var values = new List<RegistryValue>();
            foreach (var name in names)
            {
                if (name.IndexOfAny(['\r', '\n']) >= 0) throw new IOException("注册值名称无法安全导出，未清理。");
                if (--budget < 0) throw new IOException("注册项过大，请缩小处理范围。");
                var kind = key.GetValueKind(name);
                var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? throw new IOException("注册值无法完整读取。");
                var data = kind switch
                {
                    RegistryValueKind.Binary or RegistryValueKind.None => Convert.ToBase64String((byte[])value),
                    RegistryValueKind.MultiString => JsonSerializer.Serialize((string[])value),
                    RegistryValueKind.DWord => unchecked((uint)(int)value).ToString("x8"),
                    RegistryValueKind.QWord => unchecked((ulong)(long)value).ToString("x16"),
                    RegistryValueKind.String or RegistryValueKind.ExpandString => (string)value,
                    _ => throw new IOException("不支持此注册值类型，未清理。")
                };
                values.Add(new(name, kind, data));
            }
            nodes.Add(new(key.Name, values.ToArray()));
            if (!singleValue) foreach (var name in key.GetSubKeyNames().Order(StringComparer.Ordinal))
            { using var child = key.OpenSubKey(name) ?? throw new IOException("注册项读取不完整。"); Read(child, depth + 1); }
        }
        Read(selected, 0);
        return nodes.ToArray();
    }

    private static string Export(RegistryNode[] nodes)
    {
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        static string Hex(byte[] bytes) => string.Join(',', bytes.Select(value => value.ToString("x2")));
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        foreach (var node in nodes)
        {
            text.Append("\r\n[").Append(node.Path).Append("]\r\n");
            foreach (var value in node.Values)
            {
                text.Append(value.Name.Length == 0 ? "@" : Quote(value.Name)).Append('=');
                var encoded = value.Kind switch
                {
                    RegistryValueKind.String => "hex(1):" + Hex(Encoding.Unicode.GetBytes(value.Data + '\0')),
                    RegistryValueKind.ExpandString => "hex(2):" + Hex(Encoding.Unicode.GetBytes(value.Data + '\0')),
                    RegistryValueKind.MultiString => "hex(7):" + Hex(Encoding.Unicode.GetBytes(string.Join('\0', JsonSerializer.Deserialize<string[]>(value.Data)!) + "\0\0")),
                    RegistryValueKind.DWord => "dword:" + value.Data,
                    RegistryValueKind.QWord => "hex(b):" + Hex(BitConverter.GetBytes(Convert.ToUInt64(value.Data, 16))),
                    RegistryValueKind.Binary => "hex:" + Hex(Convert.FromBase64String(value.Data)),
                    RegistryValueKind.None => "hex(0):" + Hex(Convert.FromBase64String(value.Data)),
                    _ => throw new IOException("无法导出注册值。")
                };
                text.Append(encoded).Append("\r\n");
            }
        }
        return text.ToString();
    }
}
