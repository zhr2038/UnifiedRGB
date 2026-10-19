using Microsoft.Win32;
using UnifiedRgb.App.Mvvm;
using UnifiedRgb.Contracts.Dto;

namespace UnifiedRgb.App.ViewModels;

/// <summary>The HKCU Run entry that starts the tray app at logon (install.ps1 creates it as "UnifiedRgb").</summary>
public interface IAutostartRegistry
{
    string? Get();

    void Set(string? command);
}

public sealed class RunKeyAutostart : IAutostartRegistry
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "UnifiedRgb";

    public string? Get()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string;
    }

    public void Set(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (command is null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
    }
}

/// <summary>
/// Settings page. Autostart of the tray app is a per-user Run entry the GUI may toggle. Frame rate and log levels belong to
/// the elevated Host (appsettings.json under Program Files); the v1 API has no setter for them, so they are shown read-only
/// (SELF_AUDIT M7 deviation).
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IAutostartRegistry _autostart;
    private readonly string _appCommand;
    private bool _startWithWindows;
    private string _frameRate = "—";

    public SettingsViewModel(IAutostartRegistry autostart, string appExePath)
    {
        _autostart = autostart ?? throw new ArgumentNullException(nameof(autostart));
        _appCommand = $"\"{appExePath}\" --minimized";
        _startWithWindows = _autostart.Get() is not null;
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value))
            {
                _autostart.Set(value ? _appCommand : null);
            }
        }
    }

    public string FrameRate
    {
        get => _frameRate;
        private set => SetProperty(ref _frameRate, value);
    }

#pragma warning disable CA1822 // instance member for XAML binding
    public string LogLevels => "后台按配置文件记录；界面记录信息、警告与错误。";
#pragma warning restore CA1822

    public void Update(StatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        FrameRate = FormattableString.Invariant($"{status.Clock.TargetFps} fps（后台配置）");
    }
}
