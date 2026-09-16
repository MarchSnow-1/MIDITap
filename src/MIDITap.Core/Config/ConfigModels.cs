// ConfigModels.cs — 配置加载器与编辑器共用的数据类型
//
// Data types shared by the config loader/editor

namespace MIDITap.Core.Config;

/// <summary>
/// 已加载并校验的映射配置：note -> VK 码列表，外加全局字段
/// 不带显示名：显示名一律取自**文件名**，见 ConfigLocator.ListConfigFiles
///
/// A loaded, validated mapping config: note -> VK code list, plus the global fields
/// It carries no display name: that always comes from the FILE NAME, see ConfigLocator.ListConfigFiles
/// </summary>
public sealed record MappingConfig(
    IReadOnlyDictionary<byte, ushort[]> NoteMap,
    uint? Port,
    string ConfigPath);

/// <summary>
/// config 目录下的一个配置文件条目
/// Filename 带 .json，DisplayName 不带 —— 下拉框显示后者，磁盘上是前者
///
/// One config-file entry under the config directory
/// Filename carries the .json suffix while DisplayName does not
/// The drop-down shows the latter and the disk holds the former
/// </summary>
public sealed record ConfigFileInfo(string Filename, string DisplayName, string Path);

/// <summary>
/// 配置文件名不合法的原因
/// 判定在 Core，文案在界面层（Core 不依赖 i18n）
///
/// Why a config file name was rejected
/// The check lives in Core while the wording lives in the UI layer (Core does not depend on i18n)
/// </summary>
public enum ConfigNameError
{
    /// <summary>合法 / Accepted</summary>
    None,

    /// <summary>去掉 .json 后为空 / Empty once the .json suffix is removed</summary>
    Empty,

    /// <summary>含 Windows 不允许的字符，或只有点 / Holds characters Windows forbids, or is nothing but dots</summary>
    InvalidCharacters,

    /// <summary>Windows 保留设备名（CON、NUL、COM1…）/ A reserved Windows device name (CON, NUL, COM1…)</summary>
    ReservedName,

    /// <summary>以点或空格结尾，Windows 会静默去掉它们 / Ends with a dot or space, which Windows silently strips</summary>
    TrailingDotOrSpace,

    /// <summary>名字本身超出文件系统上限 / The name alone exceeds the file-system limit</summary>
    TooLong,

    /// <summary>同名文件已存在 / A file with that name already exists</summary>
    AlreadyExists,
}

/// <summary>
/// 加载选项
///
/// Load options
/// </summary>
public sealed record LoadOptions(
    bool Verbose = false,
    bool Silent = false,
    bool Strict = false,
    string? ConfigPath = null);
