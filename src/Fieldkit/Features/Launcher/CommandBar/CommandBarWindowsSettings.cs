// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// Windows' own Settings pages as rows (upstream reads the Mac's System Settings panes from disk;
/// Windows pages are addressed by documented <c>ms-settings:</c> URIs, so the list is shipped).
/// </summary>
public static class CommandBarWindowsSettings
{
    /// <param name="Uri">The ms-settings: address that opens the page.</param>
    public sealed record Page(string Uri, string Name, string Keywords);

    public static IReadOnlyList<Page> All { get; } =
    [
        new("ms-settings:display", "Display", "screen monitor resolution scale brightness night light hdr refresh rate"),
        new("ms-settings:nightlight", "Night light", "blue light warm color"),
        new("ms-settings:sound", "Sound", "audio volume speakers output input microphone"),
        new("ms-settings:apps-volume", "Volume mixer", "app volume sound"),
        new("ms-settings:notifications", "Notifications", "alerts focus do not disturb"),
        new("ms-settings:quiethours", "Focus", "do not disturb focus assist"),
        new("ms-settings:powersleep", "Power & battery", "sleep screen timeout energy saver battery power mode"),
        new("ms-settings:batterysaver", "Energy saver", "battery saver"),
        new("ms-settings:storagesense", "Storage", "disk space storage sense cleanup"),
        new("ms-settings:multitasking", "Multitasking", "snap windows alt tab virtual desktops"),
        new("ms-settings:clipboard", "Clipboard", "clipboard history sync"),
        new("ms-settings:remotedesktop", "Remote Desktop", "rdp remote"),
        new("ms-settings:about", "About", "system info pc name specifications windows version rename"),
        new("ms-settings:bluetooth", "Bluetooth & devices", "bluetooth pair devices"),
        new("ms-settings:printers", "Printers & scanners", "printer scanner print"),
        new("ms-settings:mousetouchpad", "Mouse", "mouse pointer speed scroll buttons"),
        new("ms-settings:devices-touchpad", "Touchpad", "touchpad gestures trackpad"),
        new("ms-settings:pen", "Pen & Windows Ink", "pen stylus ink"),
        new("ms-settings:autoplay", "AutoPlay", "autoplay usb removable"),
        new("ms-settings:usb", "USB", "usb devices"),
        new("ms-settings:network-status", "Network & internet", "network internet connection ethernet status"),
        new("ms-settings:network-wifi", "Wi-Fi", "wifi wireless network"),
        new("ms-settings:network-ethernet", "Ethernet", "wired network cable"),
        new("ms-settings:network-vpn", "VPN", "vpn virtual private network"),
        new("ms-settings:network-mobilehotspot", "Mobile hotspot", "hotspot tethering share internet"),
        new("ms-settings:network-airplanemode", "Airplane mode", "flight mode radios"),
        new("ms-settings:network-proxy", "Proxy", "proxy server"),
        new("ms-settings:personalization", "Personalization", "theme colors appearance"),
        new("ms-settings:personalization-background", "Background", "wallpaper desktop background picture slideshow"),
        new("ms-settings:colors", "Colors", "accent color dark mode light mode transparency"),
        new("ms-settings:themes", "Themes", "theme"),
        new("ms-settings:lockscreen", "Lock screen", "lock screen picture"),
        new("ms-settings:fonts", "Fonts", "font typeface"),
        new("ms-settings:personalization-start", "Start", "start menu pinned recommended"),
        new("ms-settings:taskbar", "Taskbar", "taskbar tray system tray icons"),
        new("ms-settings:appsfeatures", "Installed apps", "uninstall programs apps features"),
        new("ms-settings:defaultapps", "Default apps", "default browser file types"),
        new("ms-settings:optionalfeatures", "Optional features", "optional features add"),
        new("ms-settings:startupapps", "Startup apps", "startup launch at login"),
        new("ms-settings:yourinfo", "Your info", "account profile picture"),
        new("ms-settings:emailandaccounts", "Email & accounts", "email accounts"),
        new("ms-settings:signinoptions", "Sign-in options", "password pin windows hello fingerprint face"),
        new("ms-settings:otherusers", "Other users", "family users accounts"),
        new("ms-settings:sync", "Windows backup", "backup sync"),
        new("ms-settings:dateandtime", "Date & time", "clock time zone date"),
        new("ms-settings:regionformatting", "Language & region", "language region format locale"),
        new("ms-settings:keyboard", "Typing", "keyboard typing autocorrect"),
        new("ms-settings:speech", "Speech", "speech voice"),
        new("ms-settings:gaming-gamebar", "Game Bar", "xbox game bar"),
        new("ms-settings:gaming-gamemode", "Game Mode", "gaming performance"),
        new("ms-settings:easeofaccess-display", "Text size", "accessibility text size larger"),
        new("ms-settings:easeofaccess-visualeffects", "Visual effects", "accessibility animations transparency scrollbars"),
        new("ms-settings:easeofaccess-mousepointer", "Mouse pointer and touch", "accessibility cursor pointer size color"),
        new("ms-settings:easeofaccess-magnifier", "Magnifier", "accessibility zoom magnifier"),
        new("ms-settings:easeofaccess-highcontrast", "Contrast themes", "accessibility high contrast"),
        new("ms-settings:easeofaccess-narrator", "Narrator", "accessibility screen reader"),
        new("ms-settings:easeofaccess-keyboard", "Keyboard accessibility", "sticky keys filter keys on-screen keyboard"),
        new("ms-settings:privacy", "Privacy & security", "privacy security permissions"),
        new("ms-settings:privacy-location", "Location", "location gps"),
        new("ms-settings:privacy-webcam", "Camera", "camera webcam permissions"),
        new("ms-settings:privacy-microphone", "Microphone", "microphone permissions"),
        new("ms-settings:windowsdefender", "Windows Security", "antivirus defender firewall"),
        new("ms-settings:windowsupdate", "Windows Update", "updates update check"),
        new("ms-settings:recovery", "Recovery", "reset pc recovery advanced startup"),
        new("ms-settings:activation", "Activation", "product key license"),
        new("ms-settings:troubleshoot", "Troubleshoot", "fix problems troubleshooters"),
        new("ms-settings:developers", "For developers", "developer mode"),
    ];

    /// <summary>Row id for a page: "winsettings.&lt;page&gt;".</summary>
    public static string RowId(Page page) => "winsettings." + page.Uri["ms-settings:".Length..];
}
