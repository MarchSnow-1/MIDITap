// MappingEditorDialog.cs — 新增映射的对话框
// 捕获交互：音符输入靠弹一个音填入（用一个临时捕获连接；若监听已在运行，就直接用实时监听）
// 按键输入则真正夺取键盘焦点来捕获
// WinUI 的 VirtualKey 值**本身就是** Win32 的 VK 码
// 因此不需要浏览器那套规范化表
// 直接由 VirtualKeyTable 反查就得到配置里用的键名
// 组合键捕获包含修饰键
//
// MappingEditorDialog.cs — the add-mapping dialog
// Capture UX: the note input is filled by playing a note
// That uses a temporary capture connection, or the live monitor when it is already running
// The key inputs capture real keyboard focus
// WinUI's VirtualKey values ARE Win32 VK codes
// So no browser-style normalization table is needed
// The VirtualKeyTable reverse lookup produces the config vocabulary directly
// Combo capture includes modifier keys

using MIDITap.App.Services;
using MIDITap.Core.Config;
using MIDITap.Core.Keys;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MIDITap.App.Dialogs;

public sealed partial class MappingEditorDialog : ContentDialog
{
    private enum KeyCaptureMode
    {
        None,
        Single,
        Combo,
    }

    private KeyCaptureMode _captureMode = KeyCaptureMode.None;
    private bool _waitingForNote;
    // 钩子是否由本对话框安装（用于关闭时正确卸载，不误卸别人的）
    //
    // Whether the hook was installed by this dialog, so that closing unloads only its own
    private bool _hookActive;
    private readonly string? _configFilename;
    private readonly string? _configPath;
    // 是否允许「移除映射」：只有主页点音符进来的编辑流程才需要它
    //
    // Whether "remove mapping" is allowed: only the edit flow entered from a note on Home needs it
    private readonly bool _allowRemoval;

    public MappingEditorDialog(
        string? configFilename,
        string? configPath,
        byte? presetNote = null,
        bool allowRemove = false)
    {
        _configFilename = configFilename;
        _configPath = configPath;
        _allowRemoval = allowRemove;
        InitializeComponent();

        // 默认选中「单键」
        // **界面上的选中态必须与代码实际采用的一致**
        // 下面 OnKeyModeChanged 与 OnClosing 都把"不是 SelectedIndex == 1"当成单键
        // 而 RadioButtons 的默认值是"不选"
        // 于是不设这一句时，用户看到的是两个圈都空着、存进去的却是单键
        // 放在 InitializeComponent 之后而不是 XAML 的 SelectedIndex 属性上
        // XAML 里赋值会在解析过程中触发 SelectionChanged
        // 而那一刻 SinglePanel/ComboPanel 两个字段还没被赋值
        //
        // Defaults to the single-key mode
        // **What is selected on screen must match what the code uses**
        // Both OnKeyModeChanged and OnClosing treat anything but SelectedIndex == 1 as single
        // A RadioButtons starts with nothing selected
        // So without this line the user sees two empty circles while a single key is what actually gets saved
        // It is set here rather than as SelectedIndex in XAML
        // An assignment there raises SelectionChanged during parsing
        // At that moment the SinglePanel/ComboPanel fields are not assigned yet
        KeyMode.SelectedIndex = 0;

        if (presetNote is not null)
        {
            // 从音符盘点击进入时直接预填音符编号
            //
            // The note number is pre-filled when the dialog was opened by clicking a cell on the note board
            NoteBox.Text = presetNote.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        RefreshTexts();
        UpdateRemoveVisibility();

        // 键盘捕获需要收到被控件标记为已处理的事件（TextBox/按钮会吞掉空格等）
        //
        // Key capture needs the events controls have marked as handled
        // A TextBox or a button swallows keys such as space
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);
        // 任何方式关闭对话框都要停掉捕获
        // 既不能泄漏采集连接与订阅，也必须卸下键盘钩子
        // 钩子安装期间按键由本对话框全量处理
        // 关闭时不卸载它就会继续驻留，并继续吞掉系统范围内的按键
        //
        // Closing the dialog in any way must stop capture
        // The capture connection and the subscriptions must not leak, and the keyboard hook must be uninstalled
        // While the hook is installed this dialog handles every key press
        // Not uninstalling it on close would leave it resident and swallowing keys system-wide
        Closed += (_, _) =>
        {
            EndNoteCapture();
            StopCaptureHook();
        };
    }

    // ------------------------------------------------------------------ note capture

    private void OnCaptureNoteChecked(object sender, RoutedEventArgs e)
    {
        var backend = AppServices.Backend;
        _waitingForNote = true;
        NoteBox.PlaceholderText = AppServices.I18n.T("edit.note.listening");

        backend.NoteCaptured += OnBackendNoteCaptured;
        backend.NoteOn += OnBackendNoteOn;
        if (backend.IsRunning)
        {
            // 已在监听中：前端此时改为监听 midiNoteOn
            //
            // Already listening: in this case the front end switches to listening for midiNoteOn
            return;
        }
        backend.CaptureNote(backend.SelectedPort >= 0 ? backend.SelectedPort : 0);
    }

    private void OnCaptureNoteUnchecked(object sender, RoutedEventArgs e)
    {
        EndNoteCapture();
        NoteBox.PlaceholderText = AppServices.I18n.T("edit.note.placeholder");
    }

    private void OnBackendNoteCaptured(byte note) => AppServices.RunOnUi(() =>
    {
        NoteBox.Text = note.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EndNoteCapture();
    });

    private void OnBackendNoteOn(byte note, byte velocity, string? key) => AppServices.RunOnUi(() =>
    {
        if (!_waitingForNote)
        {
            return;
        }
        NoteBox.Text = note.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EndNoteCapture();
    });

    private void EndNoteCapture()
    {
        _waitingForNote = false;
        var backend = AppServices.Backend;
        backend.NoteCaptured -= OnBackendNoteCaptured;
        backend.NoteOn -= OnBackendNoteOn;
        backend.StopCapture();
        CaptureNoteBtn.IsChecked = false;
    }

    // ------------------------------------------------------------------ key capture

    private void OnSingleKeyFocus(object sender, RoutedEventArgs e)
    {
        _captureMode = KeyCaptureMode.Single;
        SingleKeyBox.PlaceholderText = AppServices.I18n.T("edit.keyListening");
        StartCaptureHook();
    }

    private void OnComboKeyFocus(object sender, RoutedEventArgs e)
    {
        _captureMode = KeyCaptureMode.Combo;
        ComboKeyBox.PlaceholderText = AppServices.I18n.T("edit.comboListening");
        StartCaptureHook();
    }

    private void OnKeyBlur(object sender, RoutedEventArgs e)
    {
        _captureMode = KeyCaptureMode.None;
        SingleKeyBox.PlaceholderText = AppServices.I18n.T("edit.keyIdle");
        ComboKeyBox.PlaceholderText = AppServices.I18n.T("edit.comboIdle");
        StopCaptureHook();
    }

    // ------------------------------------------------------------------ 捕获钩子 / Key capture hook

    /// <summary>
    /// 开始捕获时安装低级键盘钩子
    /// 它解决三个 XAML 事件做不到的事
    ///   1) 拦下 Win / Alt 等**系统级热键** —— 否则 Win 键会打开开始菜单并抢走焦点
    ///      之后所有按键都进不去对话框（这是"Win 键无法捕获"的真正原因）
    ///   2) 让修饰键（Shift/Ctrl/Alt/Win）**本身**可作为单键映射
    ///   3) 只在捕获期间接管按键，捕获结束立刻卸下 —— 不影响正常使用
    ///
    /// Installs the low-level hook while capturing
    /// It does three things XAML events cannot
    /// (1) swallow system hotkeys such as Win/Alt, which would otherwise open Start and steal focus
    /// That way no later keystroke ever reaches the dialog (the actual reason the Windows key could not be captured)
    /// (2) let a modifier itself be mapped as a single key
    /// (3) capture only while active, so normal use is unaffected
    /// </summary>
    private void StartCaptureHook()
    {
        if (KeyCaptureHook.IsActive)
        {
            return;
        }
        // 安装失败时不影响其它功能：退回 XAML 事件路径（修饰键仍可组合捕获）
        // 只是 Win 键这类系统热键依然拦不住
        //
        // A failed installation does not affect anything else: it falls back to the XAML event path
        // There modifiers can still be captured as a combo
        // Only system hotkeys such as the Windows key stay out of reach
        _hookActive = KeyCaptureHook.Install(OnHookKey);
    }

    private void StopCaptureHook()
    {
        if (_hookActive)
        {
            KeyCaptureHook.Uninstall();
            _hookActive = false;
        }
    }

    /// <summary>
    /// 钩子回调（在系统输入线程上，必须尽快返回）
    /// 返回 true 表示吞掉按键，**只在确实识别出一个键时才吞**
    /// 无法识别的按键应放行，避免用户在捕获期间连打字都打不了
    ///
    /// Hook callback (on the system input thread; must return fast)
    /// Returning true swallows the key, and we swallow ONLY when a key was actually recognised
    /// An unrecognised key is passed through so the user can still type while capture is armed
    /// </summary>
    private bool OnHookKey(CapturedKey key)
    {
        if (_captureMode == KeyCaptureMode.None)
        {
            return false;
        }
        var name = CaptureNameFor(key.VirtualKey);
        if (name is null)
        {
            return false;
        }

        if (_captureMode == KeyCaptureMode.Single)
        {
            // 只在"按下"时收集；抬起一律吞掉，避免同一个键被记录两次
            //
            // Only the key-down is collected; a key-up is swallowed rather than recorded
            // So the same key is not recorded twice
            if (key.IsDown)
            {
                AppServices.RunOnUi(() =>
                {
                    SingleKeyBox.Text = name;
                    _captureMode = KeyCaptureMode.None;
                    Root.Focus(FocusState.Programmatic);
                    SingleKeyBox.PlaceholderText = AppServices.I18n.T("edit.keyIdle");
                    StopCaptureHook();
                });
            }
            return true;
        }

        // 组合键：按下即累加 token，抬起只吞掉不记录
        //
        // Combo: a key-down appends a token, while a key-up is only swallowed and not recorded
        if (key.IsDown)
        {
            AppServices.RunOnUi(() => PushComboToken(name));
        }
        return true;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_captureMode == KeyCaptureMode.None)
        {
            return;
        }
        e.Handled = true;

        var vk = (ushort)e.Key;
        var name = CaptureNameFor(vk);
        if (name is null)
        {
            return;
        }

        if (_captureMode == KeyCaptureMode.Single)
        {
            // 修饰键作为单键映射由**钩子路径**负责（OnHookKey 不忽略修饰键）
            // 本方法只在钩子安装失败时作为回退使用，此时忽略修饰键
            // 在 XAML 路径下按 Shift/Ctrl/Alt/Win 多半会触发系统行为并抢走焦点
            // 记录下来只会让捕获卡住
            //
            // Modifiers as a single-key mapping are handled by the HOOK path (OnHookKey does not ignore them)
            // This method is only the fallback used when the hook could not be installed, and there it ignores modifier keys
            // On the XAML path a press of Shift/Ctrl/Alt/Win tends to trigger system behaviour and steal focus
            // Recording it would only leave the capture stuck
            if (IsModifier(vk))
            {
                return;
            }
            SingleKeyBox.Text = name;
            _captureMode = KeyCaptureMode.None;
            Root.Focus(FocusState.Programmatic);
            SingleKeyBox.PlaceholderText = AppServices.I18n.T("edit.keyIdle");
        }
        else
        {
            PushComboToken(name);
        }
    }

    /// <summary>
    /// 捕获用键名：0x1B/0x5B 写成 "esc"/"win"（而非反查表首选项 "escape"/"lwin"）
    /// 这里保持保存到配置文件的文本一致
    ///
    /// Capture-time key names: 0x1B/0x5B are written as "esc"/"win" rather than the lookup table's preferred "escape"/"lwin"
    /// This keeps the text saved into the config file the same
    /// </summary>
    private static string? CaptureNameFor(ushort vk) => vk switch
    {
        0x1B => "esc",
        0x5B => "win",
        _ => VirtualKeyTable.TryGetName(vk),
    };

    private static bool IsModifier(ushort vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    /// <summary>向组合键累加器追加 token，去重 / Appends a token to the combo accumulator, skipping duplicates</summary>
    private void PushComboToken(string token)
    {
        var parts = ComboKeyBox.Text.Length > 0
            ? ComboKeyBox.Text.Split('+').Where(p => p.Length > 0).ToList()
            : [];
        if (!parts.Contains(token))
        {
            parts.Add(token);
        }
        ComboKeyBox.Text = string.Join("+", parts);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        NoteBox.Text = string.Empty;
        SingleKeyBox.Text = string.Empty;
        ComboKeyBox.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnNoteTextChanged(object sender, TextChangedEventArgs e) => UpdateRemoveVisibility();

    /// <summary>
    /// 「移除映射」只在**从主页点音符进来**、且该音符在这份配置里确有映射时出现
    /// 映射页的新增流程不需要它 —— 那里每一行本来就带删除按钮，所以它不传 allowRemove
    /// 而一个没有映射可移除的音符，与其摆一个按不动的按钮，不如不显示
    /// 判定问的是后端已加载的那份配置，而不是在本对话框里另存一份映射表
    /// 两份状态一旦不同步，按钮该不该出现就会判断错
    ///
    /// "Remove mapping" appears only when the dialog was opened **from a Home note** and that note really has a mapping
    /// The mapping page's add flow does not need it — every row there already carries a delete button
    /// So it leaves allowRemove at its default
    /// For a note with nothing to remove, hiding the button beats showing one that does nothing
    /// The check asks the backend for the config it has loaded rather than keeping a second copy of the mapping table here
    /// Two copies drifting apart would get the button's visibility wrong
    /// </summary>
    private void UpdateRemoveVisibility()
    {
        RemoveMappingBtn.Visibility =
            _allowRemoval
            && byte.TryParse(NoteBox.Text.Trim(), out var note)
            && AppServices.Backend.FindMapping(note) is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// 移除当前音符在这份配置里的映射，随即关闭对话框
    /// 留着的话，对话框展示的就是一条已经不存在的绑定，看起来像改动没生效
    /// 删除后的刷新由后端的 configLoaded 广播驱动（映射表与音符盘各有一份订阅）
    /// 目标配置取自后端当前加载的那一份，与 AddMapping 收到的 _configPath 同源
    /// 两个调用点传进来的都是当前配置
    ///
    /// Removes this note's mapping, then closes the dialog
    /// Leaving it open would show a binding that no longer exists, which reads as "the change did not take"
    /// Refreshing afterwards is driven by the backend's configLoaded broadcast
    /// The mapping list and the note board each subscribe to it
    /// The target config is the one the backend has loaded — the same source as the _configPath AddMapping receives
    /// Both call sites pass the current config
    /// </summary>
    private void OnRemoveMapping(object sender, RoutedEventArgs e)
    {
        if (!byte.TryParse(NoteBox.Text.Trim(), out var note))
        {
            return;
        }
        AppServices.Backend.DeleteMapping(note.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Hide();
    }

    private void OnKeyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var combo = KeyMode.SelectedIndex == 1;
        SinglePanel.Visibility = combo ? Visibility.Collapsed : Visibility.Visible;
        ComboPanel.Visibility = combo ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ validation & save

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result != ContentDialogResult.Primary)
        {
            return;
        }
        // 校验失败时阻止关闭并显示错误
        //
        // A failed validation blocks the close and shows the error
        var note = NoteBox.Text.Trim();
        var key = KeyMode.SelectedIndex == 1 ? ComboKeyBox.Text.Trim() : SingleKeyBox.Text.Trim();

        if (note.Length == 0 || key.Length == 0)
        {
            ShowError("edit.errCaptureRequired");
            args.Cancel = true;
            return;
        }
        if (!int.TryParse(note, out var noteNum) || noteNum is < 0 or > 127)
        {
            ShowError("edit.errNoteRange");
            args.Cancel = true;
            return;
        }

        AppServices.Backend.AddMapping(
            note,
            key,
            _configFilename,
            _configPath);
    }

    private void ShowError(string key)
    {
        ErrorText.Text = AppServices.I18n.T(key);
        ErrorText.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ i18n

    public void RefreshTexts()
    {
        Func<string, string> t = AppServices.I18n.T;
        Title = t("edit.title");
        PrimaryButtonText = t("edit.save");
        CloseButtonText = t("edit.cancel");
        NoteLabel.Text = t("edit.note");
        CaptureNoteBtn.Content = t("edit.note.capture");
        NoteBox.PlaceholderText = _waitingForNote ? t("edit.note.listening") : t("edit.note.placeholder");
        KeyModeLabel.Text = t("edit.outputKey");
        ModeSingle.Content = t("edit.key");
        ModeCombo.Content = t("edit.combo");
        SingleKeyLabel.Text = t("edit.key");
        ComboKeyLabel.Text = t("edit.combo");
        SingleKeyBox.PlaceholderText = t("edit.keyIdle");
        ComboKeyBox.PlaceholderText = t("edit.comboIdle");
        ClearBtn.Content = t("edit.clear");
        RemoveMappingBtn.Content = t("edit.remove");
        if (ErrorText.Visibility == Visibility.Visible)
        {
            ShowError("edit.errCaptureRequired");
        }
    }
}
