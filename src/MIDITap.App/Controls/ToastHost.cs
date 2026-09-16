// ToastHost.cs — 右上角浮窗的宿主控件
//
// 用代码构造子元素而非 DataTemplate
// 这里每条提示的构成不同（可选操作按钮、各自的定时器）
// 代码构造比 XAML 模板加转换器更直观
//
// 配色全部走主题字典 / 系统画笔，不写死颜色
// 因此深浅色与对比度模式都能正确跟随
//
// ToastHost.cs — the host control for the toasts in the top-right corner
//
// Child elements are built in code rather than from a DataTemplate
// Every toast is composed differently (an optional action button, a timer of its own)
// Building it in code is more direct than an XAML template plus converters
//
// All colours come from the theme dictionaries or from system brushes, none is hard-coded
// So light and dark as well as contrast modes all follow correctly
//


using MIDITap.App.Services;
using MIDITap.Core.Notifications;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MIDITap.App.Controls;

public sealed class ToastHost : UserControl
{
    private readonly StackPanel _stack = new()
    {
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    // 每条浮窗一个定时器，关闭时清掉
    // 键是要移除的那一层 Border
    //
    // One timer per toast, cleared when it is dismissed
    // The key is the Border to remove
    private readonly Dictionary<Border, DispatcherTimer> _timers = new();

    public ToastHost()
    {
        // 浮窗不参与命中测试之外的布局：IsHitTestVisible 保持 true 以便点击操作按钮
        //
        // The toast takes part in no layout beyond hit testing
        // IsHitTestVisible stays true so that the action button can be clicked
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        IsTabStop = false;
        Content = _stack;

        ToastService.Raised += OnRaised;
        ToastService.Cleared += Clear;
        Unloaded += (_, _) =>
        {
            ToastService.Raised -= OnRaised;
            ToastService.Cleared -= Clear;
            Clear();
        };
    }

    private void OnRaised(ToastMessage message)
        => AppServices.RunOnUi(() => Add(message));

    /// <summary>移除全部浮窗 / Removes every toast</summary>
    public void Clear()
    {
        foreach (var timer in _timers.Values)
        {
            timer.Stop();
        }
        _timers.Clear();
        _stack.Children.Clear();
    }

    private void Add(ToastMessage message)
    {
        // 超出上限时先移除最早的，避免堆满整屏
        //
        // Removes the oldest toasts once the cap is reached, so that the corner does not fill the whole screen
        while (_stack.Children.Count >= ToastPolicy.MaxVisible)
        {
            RemoveOldest();
        }

        var card = BuildCard(message, out var dismiss);
        _stack.Children.Add(card);

        var delay = ToastPolicy.AutoDismissAfter(message.Severity, message.Action is not null);
        if (delay is { } after)
        {
            // DispatcherTimer 而非 Task.Delay：必须在 UI 线程上操作视觉树
            //
            // A DispatcherTimer rather than Task.Delay: the visual tree has to be touched on the UI thread
            var timer = new DispatcherTimer { Interval = after };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                dismiss();
            };
            _timers[card] = timer;
            timer.Start();
        }
    }

    private void RemoveOldest()
    {
        if (_stack.Children.Count == 0)
        {
            return;
        }
        if (_stack.Children[0] is Border oldest)
        {
            if (_timers.Remove(oldest, out var timer))
            {
                timer.Stop();
            }
            _stack.Children.RemoveAt(0);
        }
        else
        {
            _stack.Children.RemoveAt(0);
        }
    }

    private Border BuildCard(ToastMessage message, out Action dismiss)
    {
        var (icon, accentKey) = StyleFor(message.Severity);
        var accent = ThemeBrush(accentKey, message.Severity);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = message.Title,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(message.Message))
        {
            text.Children.Add(new TextBlock
            {
                Text = message.Message,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new FontIcon
        {
            Glyph = icon,
            FontSize = 16,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        });
        row.Children.Add(text);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(row);

        // 操作按钮先建好但不挂事件：事件处理需要引用 card，而 card 在下方才创建
        // 事件在 card 创建后统一挂接（见方法末尾）
        //
        // The action button is built first but left unsubscribed
        // Its handler needs to reference card, which is created further down
        // The handler is attached once card exists (see the end of this method)
        Button? actionButton = null;
        if (message.Action is not null && !string.IsNullOrEmpty(message.ActionLabel))
        {
            actionButton = new Button
            {
                Content = message.ActionLabel,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            body.Children.Add(actionButton);
        }

        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 11 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(body, 0);
        Grid.SetColumn(close, 1);
        grid.Children.Add(body);
        grid.Children.Add(close);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 8, 10),
            MinWidth = 260,
            MaxWidth = 420,
            Background = ThemeBrush("MiditapToastBackgroundBrush", message.Severity),
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Child = grid,
        };

        // 用实例方法 + lambda，而不是捕获 card 的局部函数
        // 局部函数要求被捕获的变量在其声明位置就已确定赋值
        // 而 card 的构造（含 grid 组合）本身就是一段表达式
        //
        // An instance method plus lambda rather than a local function capturing card
        // A local function requires its captured variable to be definitely assigned at its declaration point
        // Here card comes from a compound initialiser, so that requirement is not met
        dismiss = () => DismissCard(card);
        close.Click += (_, _) => DismissCard(card);
        if (actionButton is not null && message.Action is not null)
        {
            actionButton.Click += (_, _) =>
            {
                // 先关浮窗再执行操作：操作可能打开外部浏览器，浮窗留着已无意义
                //
                // The toast is dismissed before the action runs
                // The action may open an external browser
                // A toast kept around would then serve no purpose
                DismissCard(card);
                try
                {
                    message.Action.Invoke();
                }
                catch
                {
                    // 操作失败不应让浮窗留下半死状态
                    //
                    // A failing action should not leave the toast in a half-dead state
                }
            };
        }
        return card;
    }

    /// <summary>移除指定浮窗并停掉它的定时器 / Removes the given toast and stops its timer</summary>
    private void DismissCard(Border card)
    {
        if (_timers.Remove(card, out var timer))
        {
            timer.Stop();
        }
        _stack.Children.Remove(card);
    }

    /// <summary>各严重级别对应的图标与主题画笔键 / Icon and theme brush key for each severity</summary>
    private static (string Icon, string BrushKey) StyleFor(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => ("\uE73E", "MiditapToastSuccessBrush"),
        ToastSeverity.Warning => ("\uE7BA", "MiditapToastWarningBrush"),
        ToastSeverity.Error => ("\uEA39", "MiditapToastErrorBrush"),
        _ => ("\uE946", "MiditapToastInfoBrush"),
    };

    private static Brush ThemeBrush(string key, ToastSeverity severity)
        => ThemeService.Brush(key) ?? Fallback(severity);

    private static Brush Fallback(ToastSeverity severity) => new SolidColorBrush(severity switch
    {
        ToastSeverity.Success => Windows.UI.Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F),
        ToastSeverity.Warning => Windows.UI.Color.FromArgb(0xFF, 0xFC, 0xE1, 0x00),
        ToastSeverity.Error => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x99, 0xA4),
        _ => Windows.UI.Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA),
    });
}
