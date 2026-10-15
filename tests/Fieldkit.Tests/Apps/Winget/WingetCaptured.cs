// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Tests.Apps.Winget;

/// <summary>winget v1.29 output layouts (en-US, redirected stdout). The list and upgrade tables use sample apps;
/// search, show and sources are public catalog data.</summary>
public static class WingetCaptured
{
    /// <summary>winget upgrade --disable-interactivity layout, sample apps.</summary>
    public static readonly string Upgrade = string.Join("\r\n", new[]
    {
        "Name                                                         Id                                Version        Available      Source",
        "-----------------------------------------------------------------------------------------------------------------------------------",
        "Contoso Remote Desk                                          Contoso.RemoteDesk                cr 9.0.12      9.8.1          winget",
        "7-Zip 24.08 (x64)                                            7zip.7zip                         24.08          25.01          winget",
        "Mozilla Firefox (x64 en-US)                                  Mozilla.Firefox                   139.0.1        141.0          winget",
        "Notepad++ (64-bit x64)                                       Notepad++.Notepad++               8.7.9          8.8.3          winget",
        "Microsoft .NET Runtime - 8.0.10 (x64)                        Microsoft.DotNet.Runtime.8        8.0.10         8.0.32         winget",
        "Microsoft Visual C++ v14 Redistributable (x64) - 14.40.33810 Microsoft.VCRedist.2015+.x64      14.40.33810.0  14.51.36247.0  winget",
        "VLC media player                                             VideoLAN.VLC                      3.0.20         3.0.21         winget",
        "Fabrikam Desktop Runtime - 8.0.10 (x86)                      Fabrikam.DesktopRuntime.8         8.0.10         8.0.32         winget",
        "Python Launcher                                              Python.Launcher                   < 3.10.2       3.14.7         winget",
        "OBS Studio                                                   OBSProject.OBSStudio              30.2.3         31.1.2         winget",
        "10 upgrades available.",
    }) + "\r\n";

    /// <summary>winget list --disable-interactivity layout, sample apps (MSIX, ARP, winget, unicode).</summary>
    public static readonly string List = string.Join("\r\n", new[]
    {
        "Name                                                                                      Id                                                                                    Version             Available      Source",
        "---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------",
        "3D Builder                                                                                MSIX\\Microsoft.3DBuilder_20.0.4.0_x64__8wekyb3d8bbwe                                  20.0.4.0                                 ",
        "Fabrikam Reader                                                                           MSIX\\FabrikamReaderApp_23.1.0.0_x64__fk3tq2v9c8m1a                                    23.1.0.0                                 ",
        "Fabrikam Reader Pro (64-bit)                                                              Fabrikam.Reader.Pro                                                                   23.008.20470        25.011.20114   winget",
        "Contoso Remote Desk                                                                       Contoso.RemoteDesk                                                                    cr 9.0.12           9.8.1          winget",
        "App Installer                                                                             Microsoft.AppInstaller                                                                1.29.380.0                         winget",
        "Discord                                                                                   ARP\\User\\X64\\Discord                                                                  1.0.9261                                 ",
        "7-Zip 24.08 (x64)                                                                         7zip.7zip                                                                             24.08               25.01          winget",
        "Git                                                                                       Git.Git                                                                               2.50.1              2.55.0.5       winget",
        "Microsoft Visual Studio Code (User)                                                       Microsoft.VisualStudioCode                                                            1.139.2             1.141.1        winget",
        "Python Launcher                                                                           Python.Launcher                                                                       < 3.10.2            3.14.7         winget",
        "Café Médiathèque                                                                          Contoso.CafeMediatheque                                                               2.4.1                              winget",
        "Xbox TCUI                                                                                 MSIX\\Microsoft.Xbox.TCUI_1.24.10001.0_x64__8wekyb3d8bbwe                              1.24.10001.0                             ",
    }) + "\r\n";

    /// <summary>winget search vscode --disable-interactivity (first rows, with the Match column).</summary>
    public static readonly string Search = string.Join("\r\n", new[]
    {
        "Name                                      Id                                        Version      Match                              Source",
        "------------------------------------------------------------------------------------------------------------------------------------------",
        "Microsoft Visual Studio Code              Microsoft.VisualStudioCode                1.141.1      Moniker: vscode                    winget",
        "Visual Studio / Code for Command Palette  15722UsefulApp.WorkspaceLauncherForVSCode 1.30.0.0     Tag: vscode                        winget",
        "Codium                                    Alex313031.Codium                         1.93.1.24277 Tag: vscode                        winget",
        "Coder Desktop (Core)                      Coder.CoderDesktop                        0.8.5        Tag: vscode                        winget",
        "TheiaBlueprint                            EclipseFoundation.TheiaBlueprint          1.44.0       Tag: vscode                        winget",
        "TheiaIDE                                  EclipseFoundation.TheiaIDE                1.76.0       Tag: vscode                        winget",
    }) + "\r\n";

    /// <summary>winget show --id Git.Git --exact --source winget --disable-interactivity.</summary>
    public static readonly string Show = string.Join("\r\n", new[]
    {
        "Found Git [Git.Git]",
        "Version: 2.55.0.5",
        "Publisher: The Git Development Community",
        "Publisher Url: https://gitforwindows.org/",
        "Publisher Support Url: https://github.com/git-for-windows/git/issues",
        "Moniker: git",
        "Description:",
        "  Git is a free and open source distributed version control system designed to handle everything from small to very large projects with speed and efficiency.",
        "  Git for Windows focuses on offering a lightweight, native set of tools that bring the full feature set of the Git SCM to Windows while providing appropriate user interfaces for experienced Git users and novices alike.",
        "Homepage: https://gitforwindows.org/",
        "License: GPL-2.0",
        "License Url: https://github.com/git-for-windows/build-extra/blob/HEAD/LICENSE.txt",
        "Copyright: Copyright (C) 1989, 1991 Free Software Foundation, Inc.",
        "Release Notes:",
        "  Changes since Git for Windows v2.55.0(4) (August 11th 2026)",
        "  Following the MSYS2 project, on which Git for Windows is based, Windows 8.1 support will be dropped after Git for Windows v2.55.",
        "  New Features",
        "  - The experimental git survey command (which was never upstreamed to the core Git project) was deprecated and converted into a small shim on top of git repo structure. In the future, git repo structure should be used instead, as the git survey command will be retired eventually.",
        "  - Git for Windows' installer is now a 64-bit executable (x64 instead of 86).",
        "  - Comes with the MSYS2 runtime (Git for Windows flavor) based on Cygwin v3.6.10.",
        "  - Comes with OpenSSH v10.5.P1.",
        "  Bug Fixes",
        "  - The long-standing bug where Git Bash's screen was cleared after git.exe called vim (e.g. to edit a commit message) was fixed.",
        "  - The Bash executables in the 64-bit and ARM64 distributions no longer carry an invalid Authenticode signature.",
        "Release Notes Url: https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.5",
        "Documentation:",
        "  Wiki: https://github.com/git-for-windows/git/wiki",
        "Tags:",
        "  git",
        "  vcs",
        "Installer:",
        "  Installer Type: inno",
        "  Installer Url: https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.5/Git-2.55.0.5-64-bit.exe",
        "  Installer SHA256: d065a4e23c3d9a6b5073d609b5be0830227ec3ca053c083ba385061ddfaf94c6",
        "  Release Date: 2026-08-20",
        "  Offline Distribution Supported: true",
    }) + "\r\n";

    /// <summary>winget source list --disable-interactivity.</summary>
    public static readonly string Sources = string.Join("\r\n", new[]
    {
        "Name        Argument                                      Explicit",
        "------------------------------------------------------------------",
        "msstore     https://storeedgefd.dsx.mp.microsoft.com/v9.0 false",
        "winget      https://cdn.winget.microsoft.com/cache        false",
        "winget-font https://cdn.winget.microsoft.com/fonts        true",
    }) + "\r\n";
}
