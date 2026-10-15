// SPDX-License-Identifier: GPL-3.0-or-later
using System.Media;
using System.Windows;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>
/// One wheel on screen at a time (upstream <c>RadialMenuService</c> session half). At rest it holds
/// nothing but a hidden overlay; while a wheel is open it owns a keyboard hook (Esc, Enter, arrows,
/// digits, summoner release), a mouse hook (pointer tracking, clicks) and a foreground watcher, and
/// drops all three the moment the wheel closes. Every method runs on the UI thread.
/// </summary>
internal static class RadialMenuSession
{
    private const int VkEscape = 0x1B;
    private const int VkReturn = 0x0D;

    private static readonly List<List<RadialMenuItem>> Stack = [];
    private static readonly List<string> Trail = [];
    private static RadialMenuWindow? _window;
    private static RadialCatalog _catalog = new([], [], _ => false);
    private static RadialMenuProfile? _profile;
    private static int? _highlighted;
    private static RadialActivationMode _mode;
    private static Shortcut? _holdShortcut;
    private static RadialMouseButton? _holdButton;
    private static Point _center;
    private static Point _openPointer;
    private static double _scale = 1;
    private static bool _pointerArmed;
    private static int _sessionId;
    private static RadialNowPlayingState _nowPlaying = RadialNowPlayingState.NothingPlaying;
    private static bool _nowPlayingPending;
    private static RadialNowPlayingCard? _card;
    private static IDisposable? _cardHook;

    private static IDisposable? _keyHook;
    private static IDisposable? _mouseHook;
    private static IntPtr _foregroundHook;
    private static Native.WinEventDelegate? _foregroundProc;

    // Read by the hook thread.
    private static volatile bool _active;
    private static volatile bool _holdPhase;
    private static volatile int _count;
    private static volatile int _holdKey;
    private static int _panelLeft, _panelTop, _panelRight, _panelBottom;
    private static int _moveX, _moveY, _movePending, _swallowLeftUp;
    private static readonly HashSet<int> SwallowedKeys = [];

    public static bool IsActive => _active;
    public static bool InHoldPhase => _holdPhase;
    public static Guid? ActiveProfileId => _active ? _profile?.Id : null;

    private static List<RadialMenuItem> CurrentItems => Stack.Count > 0 ? Stack[^1] : [];

    // ---------------------------------------------------------------- session

    /// <summary>Opens <paramref name="profile"/>'s wheel. <paramref name="hold"/>: summoned by a held
    /// shortcut; <paramref name="heldButton"/>: summoned by a mouse button still down.</summary>
    public static void Begin(RadialMenuProfile profile, bool hold, RadialMouseButton? heldButton = null)
    {
        var catalog = RadialCatalog.Current;
        var items = RadialMenuSupport.AvailableItems(profile.Items, catalog.IsRunnable);
        if (items.Count == 0)
        {
            SystemSounds.Beep.Play();
            return;
        }
        if (_active) Teardown(animated: false);
        DismissCard();
        _catalog = catalog;
        _profile = profile;
        _sessionId++;
        var sessionId = _sessionId;

        _mode = RadialRaw.ParseActivation(Settings.GetString(RadialMenuStore.ActivationModeKey, "pressOrHold"));
        var shortcut = Shortcut.Parse(profile.Shortcut);
        if (shortcut.IsEmpty) shortcut = Shortcut.Parse(RadialMenuSupport.DefaultShortcut);
        var startsHeld = RadialMenuSupport.StartsHeld(_mode, hold, heldButton is not null, shortcut.Modifiers != Modifiers.None);
        _holdShortcut = startsHeld && heldButton is null ? shortcut : null;
        _holdButton = startsHeld ? heldButton : null;
        _holdPhase = startsHeld;
        _holdKey = _holdShortcut?.Key ?? 0;

        Stack.Clear();
        Stack.Add(items);
        Trail.Clear();
        _highlighted = null;
        _count = items.Count;
        _pointerArmed = false;
        Native.GetCursorPos(out var cursor);
        _openPointer = new Point(cursor.X, cursor.Y);

        // Placement in physical pixels on the pointer's monitor, kept whole near edges.
        var work = WindowHelper.GetWorkAreaAt(cursor.X, cursor.Y);
        var area = new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        _scale = WindowHelper.GetScaleAt(cursor.X, cursor.Y);
        var wanted = Settings.GetBool(RadialMenuStore.AtPointerKey, true)
            ? _openPointer
            : new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var panel = RadialLayout.PanelSize * _scale;
        _center = RadialGeometry.ClampCenter(wanted, area, panel);
        _panelLeft = (int)(_center.X - panel / 2);
        _panelTop = (int)(_center.Y - panel / 2);
        _panelRight = (int)(_center.X + panel / 2);
        _panelBottom = (int)(_center.Y + panel / 2);

        _window ??= new RadialMenuWindow();
        var wheel = _window.NewWheel(catalog, ThemeManager.IsDark);
        wheel.SetItems(items, null, profile.Color);

        _nowPlaying = RadialNowPlayingState.NothingPlaying;
        _nowPlayingPending = false;
        if (RadialMenuSupport.ContainsNowPlaying(items))
        {
            _nowPlaying = RadialNowPlayingState.Loading;
            wheel.SetNowPlaying(_nowPlaying, null);
            _ = LoadNowPlaying(sessionId);
        }

        _window.Opacity = 0;
        _window.PlacePhysical(_center.X - panel / 2, _center.Y - panel / 2, RadialLayout.PanelSize, RadialLayout.PanelSize, _scale);
        _window.ShowQuietly();
        _window.FadeIn(0.18);
        wheel.PlayOpen();

        _active = true;
        InstallHooks();
        RefreshHighlight(_openPointer);
        // The chord may already be gone by the time the hotkey message arrived.
        if (_holdPhase && _holdShortcut is { } held && (Shortcut.CurrentModifiers() & held.Modifiers) != held.Modifiers)
            Application.Current?.Dispatcher.BeginInvoke(EndHoldPhase);
    }

    /// <summary>Closes the wheel (fading out) and releases every session hook.</summary>
    public static void End() => Teardown(animated: true);

    /// <summary>The feature stopped: close everything, including the overlay windows.</summary>
    public static void Shutdown()
    {
        Teardown(animated: false);
        DismissCard();
        _window?.Close();
        _window = null;
    }

    private static void Teardown(bool animated)
    {
        if (!_active) return;
        _active = false;
        _holdPhase = false;
        _holdKey = 0;
        RemoveHooks();
        var window = _window;
        void Hide()
        {
            // A new session may have claimed the overlay while this one faded.
            if (_active) return;
            window?.Hide();
            // A Try it session can run with the feature off; nothing stays resident for it.
            if (FeatureRuntime.Service<RadialMenuService>() is not { IsRunning: true } && ReferenceEquals(_window, window))
            {
                _window?.Close();
                _window = null;
            }
        }
        if (window is not null && animated && window.Wheel is { } wheel)
        {
            wheel.PlayClose(() => { });
            window.FadeOut(0.13, Hide);
        }
        else
        {
            Hide();
        }
        Stack.Clear();
        Trail.Clear();
        _highlighted = null;
        _holdShortcut = null;
        _holdButton = null;
        _count = 0;
    }

    /// <summary>A mouse-button session's summoner went up.</summary>
    public static void ButtonReleased(RadialMouseButton button)
    {
        if (_active && _holdPhase && _holdButton == button) EndHoldPhase();
    }

    /// <summary>Resolves the summoner release with the mode captured when the session opened.</summary>
    private static void EndHoldPhase()
    {
        if (!_active || !_holdPhase) return;
        switch (RadialMenuSupport.ReleaseAction(_mode, _highlighted is not null))
        {
            case RadialReleaseAction.StayOpen:
                EnterStickyPhase();
                break;
            case RadialReleaseAction.Dismiss:
                End();
                break;
            case RadialReleaseAction.Select:
                Select(_highlighted!.Value);
                break;
        }
    }

    private static void EnterStickyPhase()
    {
        _holdPhase = false;
        _holdButton = null;
        _holdShortcut = null;
        _holdKey = 0;
    }

    // -------------------------------------------------------------- selection

    private static void RefreshHighlight(Point pointer)
    {
        if (!_active || _window?.Wheel is not { } wheel) return;
        if (!_pointerArmed)
        {
            // Until the pointer really travels it owns nothing, so a wheel opened in the middle of
            // the screen never fires on whatever direction the pointer already sat in.
            if (!RadialGeometry.PointerArmed(pointer.X - _openPointer.X, pointer.Y - _openPointer.Y, RadialLayout.MoveActivationDistance * _scale))
                return;
            _pointerArmed = true;
        }
        var index = RadialGeometry.HighlightedIndex((pointer.X - _center.X) / _scale, (_center.Y - pointer.Y) / _scale,
            RadialLayout.DeadZoneRadius, CurrentItems.Count);
        SetHighlight(index);
    }

    private static void SetHighlight(int? index)
    {
        _highlighted = index;
        _window?.Wheel?.SetHighlight(index);
    }

    private static void PointerMoved(Point pointer)
    {
        if (!_active) return;
        // A chord release can slip past the keyboard hook; the next pointer move settles it.
        if (_holdPhase && _holdShortcut is { } held && (Shortcut.CurrentModifiers() & held.Modifiers) != held.Modifiers)
        {
            EndHoldPhase();
            if (!_active) return;
        }
        RefreshHighlight(pointer);
    }

    /// <summary>A click: past the wheel's edge dismisses, the hub steps back, a highlighted slice runs.</summary>
    private static void ActivatePointer(Point pointer)
    {
        if (!_active) return;
        RefreshHighlight(pointer);
        switch (RadialGeometry.Zone((pointer.X - _center.X) / _scale, (_center.Y - pointer.Y) / _scale))
        {
            case RadialPointerZone.Outside: End(); break;
            case RadialPointerZone.Hub: StepBack(); break;
            default:
                if (_highlighted is { } index) Select(index);
                break;
        }
    }

    private static void Select(int index)
    {
        if (!_active || index < 0 || index >= CurrentItems.Count || _profile is null) return;
        var item = CurrentItems[index];
        if (item.Kind == RadialItemKind.Submenu)
        {
            Stack.Add(item.Children);
            Trail.Add(item.Name);
            _count = item.Children.Count;
            _highlighted = null;
            // Browsing children is sticky by nature; re-arming keeps a double-click from running
            // the child that happens to sit in the parent slice's direction.
            EnterStickyPhase();
            _pointerArmed = false;
            Native.GetCursorPos(out var p);
            _openPointer = new Point(p.X, p.Y);
            _window?.Wheel?.SetItems(item.Children, item.Name, _profile.Color);
            _window?.Wheel?.SetNowPlaying(_nowPlaying, NowPlayingIcon());
            return;
        }
        var catalog = _catalog;
        End();
        RadialActions.Run(item, catalog, _ => PresentNowPlaying());
    }

    /// <summary>Esc or a click on the hub: leave the submenu, then the wheel.</summary>
    private static void StepBack()
    {
        if (!_active || _profile is null) return;
        if (Stack.Count > 1)
        {
            Stack.RemoveAt(Stack.Count - 1);
            Trail.RemoveAt(Trail.Count - 1);
            _count = CurrentItems.Count;
            _highlighted = null;
            _window?.Wheel?.SetItems(CurrentItems, Trail.Count > 0 ? Trail[^1] : null, _profile.Color);
            _window?.Wheel?.SetNowPlaying(_nowPlaying, NowPlayingIcon());
            Native.GetCursorPos(out var p);
            RefreshHighlight(new Point(p.X, p.Y));
        }
        else
        {
            End();
        }
    }

    private static void Rotate(int delta)
    {
        if (!_active) return;
        // In adaptive mode, reaching for the keyboard switches to sticky browsing; strict hold keeps
        // the release-to-run contract.
        if (_mode != RadialActivationMode.Hold) EnterStickyPhase();
        SetHighlight(RadialMenuSupport.RotateHighlight(_highlighted, delta, CurrentItems.Count));
    }

    // -------------------------------------------------------------- now playing

    private static async Task LoadNowPlaying(int sessionId)
    {
        var snapshot = await RadialNowPlaying.FetchAsync();
        Ui.OnUi(() =>
        {
            if (_sessionId != sessionId) return;
            _nowPlaying = snapshot is null ? RadialNowPlayingState.NothingPlaying : RadialNowPlayingState.Playing(snapshot);
            if (_active) _window?.Wheel?.SetNowPlaying(_nowPlaying, NowPlayingIcon());
            if (_nowPlayingPending)
            {
                _nowPlayingPending = false;
                PresentNowPlaying();
            }
        });
    }

    private static System.Windows.Media.ImageSource? NowPlayingIcon() =>
        _nowPlaying.Snapshot is { } s ? RadialNowPlaying.AppIcon(s) : null;

    private static void PresentNowPlaying()
    {
        switch (_nowPlaying.Kind)
        {
            case RadialNowPlayingKind.Loading:
                _nowPlayingPending = true;
                return;
            case RadialNowPlayingKind.Playing when _nowPlaying.Snapshot is { } snapshot:
                ShowCard(snapshot);
                return;
        }
    }

    private static void ShowCard(RadialNowPlayingSnapshot snapshot)
    {
        DismissCard();
        var card = new RadialNowPlayingCard(snapshot, () =>
        {
            DismissCard();
            RadialNowPlaying.OpenApp(snapshot);
        });
        card.Opacity = 0;
        card.ShowQuietly();
        card.UpdateLayout();
        var size = new Size(card.ActualWidth * _scale, card.ActualHeight * _scale);
        var work = WindowHelper.GetWorkAreaAt((int)_center.X, (int)_center.Y);
        var area = new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        var origin = RadialGeometry.PlaceCard(_center, size, area, 12 * _scale);
        card.PlacePhysical(origin.X, origin.Y, card.ActualWidth, card.ActualHeight, _scale);
        card.FadeIn(0.12);
        _card = card;
        var left = (int)origin.X;
        var top = (int)origin.Y;
        var right = (int)(origin.X + size.Width);
        var bottom = (int)(origin.Y + size.Height);
        // Any click elsewhere dismisses the card (the click itself goes through).
        _cardHook = InputHooks.AddMouseHandler((ref MouseEvent e) =>
        {
            if (e.Action is MouseAction.LeftDown or MouseAction.RightDown or MouseAction.MiddleDown or MouseAction.XDown
                && !(e.X >= left && e.X < right && e.Y >= top && e.Y < bottom))
                Ui.OnUi(DismissCard);
            return false;
        }, priority: 60);
    }

    private static void DismissCard()
    {
        _nowPlayingPending = false;
        _cardHook?.Dispose();
        _cardHook = null;
        _card?.Close();
        _card = null;
    }

    // ------------------------------------------------------------------ hooks

    private static void InstallHooks()
    {
        RemoveHooks();
        _keyHook = InputHooks.AddKeyboardHandler(OnKey, priority: 50);
        _mouseHook = InputHooks.AddMouseHandler(OnMouse, priority: 50);
        _foregroundProc = OnForegroundChanged;
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
    }

    private static void RemoveHooks()
    {
        _keyHook?.Dispose();
        _keyHook = null;
        _mouseHook?.Dispose();
        _mouseHook = null;
        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        _foregroundProc = null;
        lock (SwallowedKeys) SwallowedKeys.Clear();
        Interlocked.Exchange(ref _swallowLeftUp, 0);
    }

    /// <summary>Another app came to the front: the wheel belongs to the one it opened over.</summary>
    private static void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_active) End();
    }

    // Hook thread.
    private static bool OnKey(ref KeyEvent e)
    {
        if (!_active || e.IsOwn) return false;
        var vk = e.VirtualKey;
        if (!e.IsDown)
        {
            lock (SwallowedKeys)
            {
                if (SwallowedKeys.Remove(vk)) return true;
            }
            if (_holdPhase && ModifierOf(vk) is var released and not Modifiers.None)
                Ui.OnUi(() => CheckHoldRelease(released));
            return false;
        }

        // The summoner's own key repeating while it is held belongs to the wheel.
        if (_holdPhase && vk == _holdKey) return Swallow(vk);
        switch (vk)
        {
            case VkEscape:
                Ui.OnUi(StepBack);
                return Swallow(vk);
            case VkReturn:
                Ui.OnUi(() =>
                {
                    if (_highlighted is { } index) Select(index);
                });
                return Swallow(vk);
            case 0x25 or 0x26:
                Ui.OnUi(() => Rotate(-1));
                return Swallow(vk);
            case 0x27 or 0x28:
                Ui.OnUi(() => Rotate(1));
                return Swallow(vk);
        }
        if (RadialMenuSupport.DigitIndex(vk, _count) is { } digit)
        {
            Ui.OnUi(() => Select(digit));
            return Swallow(vk);
        }
        return false;
    }

    private static bool Swallow(int vk)
    {
        lock (SwallowedKeys) SwallowedKeys.Add(vk);
        return true;
    }

    private static Modifiers ModifierOf(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => Modifiers.Shift,
        0x11 or 0xA2 or 0xA3 => Modifiers.Ctrl,
        0x12 or 0xA4 or 0xA5 => Modifiers.Alt,
        0x5B or 0x5C => Modifiers.Win,
        _ => Modifiers.None,
    };

    private static void CheckHoldRelease(Modifiers released)
    {
        if (!_active || !_holdPhase || _holdShortcut is not { } held) return;
        var still = Shortcut.CurrentModifiers() & ~released;
        if ((still & held.Modifiers) != held.Modifiers) EndHoldPhase();
    }

    // Hook thread.
    private static bool OnMouse(ref MouseEvent e)
    {
        if (!_active || e.IsOwn) return false;
        var inside = e.X >= _panelLeft && e.X < _panelRight && e.Y >= _panelTop && e.Y < _panelBottom;
        switch (e.Action)
        {
            case MouseAction.Move:
                Interlocked.Exchange(ref _moveX, e.X);
                Interlocked.Exchange(ref _moveY, e.Y);
                if (Interlocked.Exchange(ref _movePending, 1) == 0)
                {
                    Ui.OnUi(() =>
                    {
                        Interlocked.Exchange(ref _movePending, 0);
                        PointerMoved(new Point(Volatile.Read(ref _moveX), Volatile.Read(ref _moveY)));
                    });
                }
                return false;
            case MouseAction.LeftDown:
                if (inside)
                {
                    var p = new Point(e.X, e.Y);
                    Interlocked.Exchange(ref _swallowLeftUp, 1);
                    Ui.OnUi(() => ActivatePointer(p));
                    return true;
                }
                Ui.OnUi(End);
                return false;
            case MouseAction.LeftUp:
                return Interlocked.Exchange(ref _swallowLeftUp, 0) == 1;
            case MouseAction.RightDown or MouseAction.MiddleDown or MouseAction.XDown:
                if (!inside) Ui.OnUi(End);
                return false;
            default:
                return false;
        }
    }
}
