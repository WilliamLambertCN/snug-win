using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SnugWin.App.Controllers;
using SnugWin.Core.Config;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Windows.Support;

namespace SnugWin.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppController _controller;
    private readonly Action _uiChangedHandler;
    private readonly Action<UpdateCheckResult?> _updateHandler;
    private readonly Action<string> _logHandler;
    private readonly Action<bool> _themeHandler;
    private bool _suppressEvents;
    private bool _isUpdating;

    public SettingsWindow(AppController controller, int initialTab = 0)
    {
        InitializeComponent();
        _controller = controller;
        WindowPlacementHelper.ApplyMicaBackdrop(this, _controller.IsEffectiveDarkTheme);
        _uiChangedHandler = () => Dispatcher.BeginInvoke(Refresh);
        _updateHandler = upd => Dispatcher.BeginInvoke(() => RefreshUpdateUi(upd));
        _logHandler = line => Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
        _themeHandler = isDark => Dispatcher.BeginInvoke(() =>
        {
            WindowPlacementHelper.ApplyMicaBackdrop(this, isDark);
            Refresh();
        });

        _controller.UiStateChanged += _uiChangedHandler;
        _controller.UpdateStatusChanged += _updateHandler;
        _controller.LogAppended += _logHandler;
        _controller.ThemeChanged += _themeHandler;
        Closed += (_, _) =>
        {
            _controller.UiStateChanged -= _uiChangedHandler;
            _controller.UpdateStatusChanged -= _updateHandler;
            _controller.LogAppended -= _logHandler;
            _controller.ThemeChanged -= _themeHandler;
        };

        LogBox.Text = string.Join(Environment.NewLine, _controller.GetRecentLogs()) + Environment.NewLine;
        LogBox.ScrollToEnd();
        Refresh();
        SelectTab(initialTab);
    }

    public sealed record ScreenRowItem(
        string Id,
        string Name,
        string KindOrientationBadge,
        string DeviceSummary,
        string StatusLabel,
        bool Enabled,
        Visibility ReadyBadgeVisibility,
        Visibility PreviewVisibility,
        Visibility DeleteVisibility);

    public void SelectTab(int index)
    {
        PageHome.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageGestures.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageDiagnostics.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageScreens.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        bool isHome = index == 0;
        HeaderBrandGroup.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        HeaderSubpageGroup.Visibility = isHome ? Visibility.Collapsed : Visibility.Visible;
        SubpageTitleText.Text = index switch
        {
            1 => "手势与交互细项",
            2 => "驱动状态与实时日志",
            3 => "关于与版本更新",
            4 => "多屏与设备管理",
            _ => string.Empty
        };
    }

    private void OnBackHome(object sender, RoutedEventArgs e) => SelectTab(0);
    private void OnOpenGesturesTab(object sender, RoutedEventArgs e) => SelectTab(1);
    private void OnOpenDiagnosticsTab(object sender, RoutedEventArgs e) => SelectTab(2);
    private void OnOpenAboutTab(object sender, RoutedEventArgs e) => SelectTab(3);
    private void OnOpenScreensTab(object sender, RoutedEventArgs e) => SelectTab(4);
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnCloseToTrayClick(object sender, RoutedEventArgs e) => Close();

    private void Refresh()
    {
        _suppressEvents = true;
        try
        {
            RefreshStatus();
            RefreshDriverStatus();

            var cfg = _controller.Config;
            PhoneScreenCheck.IsChecked = cfg.PhoneScreen;
            AutoStartCheck.IsChecked = _controller.IsAutoStartEnabled;
            GesturesCheck.IsChecked = cfg.Gestures;
            GestureScrollCheck.IsChecked = cfg.GestureScroll;
            GestureRightClickCheck.IsChecked = cfg.GestureLongPressRightClick;
            CopyAfterSelectionCheck.IsChecked = cfg.CopyAfterSelection;
            InjectedOnlyCheck.IsChecked = cfg.GesturesInjectedOnly;
            GestureScrollCheck.IsEnabled = cfg.Gestures;
            GestureRightClickCheck.IsEnabled = cfg.Gestures;
            CopyAfterSelectionCheck.IsEnabled = cfg.Gestures;
            InjectedOnlyCheck.IsEnabled = cfg.Gestures;
            ScreenshotCheck.IsChecked = cfg.Screenshot;
            ManualPhoneCheck.IsChecked = _controller.ManualOverride;
            AutoCheckUpdatesCheck.IsChecked = cfg.AutoCheckUpdates;

            RefreshPrimaryScreen(_controller.GetScreenConfig(_controller.PrimaryTouchScreenId)
                ?? new ScreenConfig { Device = cfg.Phone });

            var screens = _controller.ConfiguredScreens;
            int enabledCount = 0;
            foreach (var s in screens) if (s.Enabled) enabledCount++;
            ScreensSummaryBadge.Text = screens.Count > 1
                ? $"多屏 {enabledCount}/{screens.Count} ›"
                : $"多屏 {screens.Count}/{ScreenConfig.Limit} ›";

            RefreshScreensPage(screens);

            ZoomValueText.Text = cfg.ZoomSteps == 0 ? "0" : $"+{cfg.ZoomSteps}";
            ZoomMinusBtn.IsEnabled = cfg.ZoomSteps > AppConfig.MinZoomSteps;
            ZoomPlusBtn.IsEnabled = cfg.ZoomSteps < AppConfig.MaxZoomSteps;

            ThemeLightBtn.IsChecked = cfg.Theme == AppThemeMode.Light;
            ThemeDarkBtn.IsChecked = cfg.Theme == AppThemeMode.Dark;
            ThemeAutoBtn.IsChecked = cfg.Theme == AppThemeMode.Auto;

            RefreshUpdateUi(_controller.LatestUpdate);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void RefreshStatus()
    {
        VersionBadgeText.Text = $"v{_controller.CurrentVersion}";
        string pkgLabel = _controller.PackageKind == ReleasePackageKind.SelfContained ? "自包含版" : "框架依赖版";
        AboutVersionText.Text = $"当前版本：v{_controller.CurrentVersion}（{pkgLabel}）";
        StatusText.Text = _controller.GetStatusSummary();
        StatusIndicatorDot.Fill = _controller.IsPhoneScreenReady
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA));
        ElevationText.Text = _controller.IsElevated ? "管理员权限运行中" : "普通用户权限";
        ElevateBtn.Visibility = _controller.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        HeroReentryBtn.Visibility = _controller.CanReenterPhoneScreen ? Visibility.Visible : Visibility.Collapsed;
        HeroReadyBadge.Visibility = !_controller.CanReenterPhoneScreen && _controller.IsElevated
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshDriverStatus()
    {
        bool pipeOk = _controller.IsDriverPipeAvailable;
        DiagPipeText.Text = pipeOk
            ? @"已连接命名管道 \\.\pipe\virtualdisplaydriver"
            : @"未检测到命名管道 \\.\pipe\virtualdisplaydriver";
        DiagAlertDot.Visibility = pipeOk ? Visibility.Collapsed : Visibility.Visible;
        DriverWarningBanner.Visibility = pipeOk ? Visibility.Collapsed : Visibility.Visible;
        StatusHeroCard.Visibility = pipeOk ? Visibility.Visible : Visibility.Collapsed;
        if (pipeOk) return;
        bool bundled = _controller.BundledDriverInstallerScript is not null;
        DriverWarningText.Text = bundled
            ? "已内置 virtual-display-rs 0.3.1，点击右侧一键校验安装"
            : "请安装或重启 virtual-display-rs 0.3.1 虚拟显示器驱动";
        if (InstallDriverBtn.IsEnabled) InstallDriverBtn.Content = bundled ? "一键安装" : "下载驱动";
    }

    private void RefreshPrimaryScreen(ScreenConfig screen)
    {
        PhoneModelText.Text = screen.Device.Model ?? "自定义比例";
        var fit = ScreenPlan.FitOf(screen);
        var (pxW, pxH) = fit.PixelSize(fit.BaseWidth);
        PhoneSpecText.Text = $"{pxW} × {pxH} · {fit.TargetDpiPercent(fit.BaseWidth)}%";
        double ratio = (double)pxW / Math.Max(1, pxH);
        if (ratio >= 1.0)
        {
            PhoneFrameOutline.Width = 28;
            PhoneFrameOutline.Height = Math.Clamp(Math.Round(28 / ratio), 14, 28);
        }
        else
        {
            PhoneFrameOutline.Height = 34;
            PhoneFrameOutline.Width = Math.Clamp(Math.Round(34 * ratio), 14, 28);
        }
    }

    private void RefreshUpdateUi(UpdateCheckResult? upd)
    {
        if (_isUpdating) return;
        if (upd is null)
        {
            UpdateDot.Visibility = Visibility.Collapsed;
            InstallUpdateBtn.Visibility = Visibility.Collapsed;
            return;
        }

        if (upd.HasUpdate)
        {
            UpdateDot.Visibility = Visibility.Visible;
            UpdateStateText.Text = $"发现新版本 v{upd.LatestVersion}（点击可直接下载、校验 SHA-256 并重启升级）";
            InstallUpdateBtn.Visibility = string.IsNullOrWhiteSpace(upd.PackageDownloadUrl)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        else
        {
            UpdateDot.Visibility = Visibility.Collapsed;
            UpdateStateText.Text = $"已是最新版本（v{upd.CurrentVersion}）";
            InstallUpdateBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPhoneScreenChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents) _controller.SetPhoneScreenEnabled(PhoneScreenCheck.IsChecked == true);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool target = AutoStartCheck.IsChecked == true;
        try
        {
            _controller.SetAutoStartEnabled(target);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"设置开机自启动失败：{ex.Message}", "SnugWin", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }
    }

    private void OnGesturesChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents) _controller.SetGesturesEnabled(GesturesCheck.IsChecked == true);
    }

    private void OnGestureScrollChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents) _controller.SetGestureScrollEnabled(GestureScrollCheck.IsChecked == true);
    }

    private void OnGestureRightClickChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents) _controller.SetGestureLongPressRightClickEnabled(GestureRightClickCheck.IsChecked == true);
    }

    private void OnCopyAfterSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents) _controller.SetCopyAfterSelectionEnabled(CopyAfterSelectionCheck.IsChecked == true);
    }

    private void OnInjectedOnlyChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents) _controller.SetGesturesInjectedOnlyEnabled(InjectedOnlyCheck.IsChecked == true);
    }

    private void OnScreenshotChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents) _controller.SetScreenshotEnabled(ScreenshotCheck.IsChecked == true);
    }

    private void OnManualChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents) _controller.SetManualOverride(ManualPhoneCheck.IsChecked == true);
    }

    private void OnAutoCheckUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents) _controller.SetAutoCheckUpdatesEnabled(AutoCheckUpdatesCheck.IsChecked == true);
    }

    private void OnZoomMinus(object sender, RoutedEventArgs e) =>
        _controller.SetDefaultZoomSteps(_controller.Config.ZoomSteps - 1);

    private void OnZoomPlus(object sender, RoutedEventArgs e) =>
        _controller.SetDefaultZoomSteps(_controller.Config.ZoomSteps + 1);

    private void OnThemeModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender == ThemeLightBtn) _controller.SetThemeMode(AppThemeMode.Light);
        else if (sender == ThemeDarkBtn) _controller.SetThemeMode(AppThemeMode.Dark);
        else if (sender == ThemeAutoBtn) _controller.SetThemeMode(AppThemeMode.Auto);
    }

    private void OnReenterClick(object sender, RoutedEventArgs e) => _controller.ReenterScreen();
    private void OnOpenPreview(object sender, RoutedEventArgs e) => _controller.OpenScreenPreview(_controller.PrimaryTouchScreenId);

    private void OnOpenModels(object sender, RoutedEventArgs e) =>
        new PhoneModelWindow(_controller, _controller.PrimaryTouchScreenId) { Owner = this }.ShowDialog();

    private void RefreshScreensPage(IReadOnlyList<ScreenConfig> screens)
    {
        var rows = new List<ScreenRowItem>(screens.Count);
        foreach (var s in screens)
        {
            var state = _controller.GetScreenState(s.Id);
            string kindLabel = s.Device.Kind.DisplayName();
            string orientLabel = s.Orientation.DisplayName();
            var fit = ScreenPlan.FitOf(s);
            var (pxW, pxH) = fit.PixelSize(fit.BaseWidth);
            string summary = $"{s.Device.Model ?? "自定义"} · {pxW} × {pxH}";
            string status = ScreenStatusLabel(state.ScreenPhase, s.Enabled);
            rows.Add(new ScreenRowItem(
                Id: s.Id,
                Name: s.Name,
                KindOrientationBadge: $"{kindLabel} · {orientLabel}",
                DeviceSummary: summary,
                StatusLabel: status,
                Enabled: s.Enabled,
                ReadyBadgeVisibility: state.ScreenPhase == PhoneScreenPhase.Ready ? Visibility.Visible : Visibility.Collapsed,
                PreviewVisibility: s.Device.Kind.IsTouch() ? Visibility.Visible : Visibility.Collapsed,
                DeleteVisibility: screens.Count > 1 ? Visibility.Visible : Visibility.Collapsed));
        }

        ScreensList.ItemsSource = rows;
        AddScreenLimitHint.Text = $"{screens.Count} / {ScreenConfig.Limit}";
        bool canAdd = screens.Count < ScreenConfig.Limit;
        AddScreenFormPanel.Visibility = canAdd ? Visibility.Visible : Visibility.Collapsed;
        AddScreenFullText.Visibility = canAdd ? Visibility.Collapsed : Visibility.Visible;
        UpdateAddOrientationAvailability();
    }

    private static string ScreenStatusLabel(PhoneScreenPhase phase, bool enabled) => phase switch
    {
        PhoneScreenPhase.Ready => "已连接",
        PhoneScreenPhase.Preparing => "创建中",
        PhoneScreenPhase.Failed => "异常",
        _ => enabled ? "待命" : "已停用"
    };

    private DeviceKind SelectedAddKind()
    {
        if (AddTabletKindBtn.IsChecked == true) return DeviceKind.Tablet;
        if (AddComputerKindBtn.IsChecked == true) return DeviceKind.Computer;
        return DeviceKind.Phone;
    }

    private void UpdateAddOrientationAvailability()
    {
        var kind = SelectedAddKind();
        if (kind == DeviceKind.Computer)
        {
            AddLandscapeBtn.IsChecked = true;
            AddPortraitBtn.IsEnabled = false;
        }
        else
        {
            AddPortraitBtn.IsEnabled = true;
        }
    }

    private void OnAddKindChanged(object sender, RoutedEventArgs e) => UpdateAddOrientationAvailability();

    private void OnScreenEnabledClick(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is System.Windows.Controls.CheckBox { Tag: string id } cb)
            _controller.SetScreenEnabled(id, cb.IsChecked == true);
    }

    private void OnPreviewScreenClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string id })
            _controller.OpenScreenPreview(id);
    }

    private void OnSwapScreenModelClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string id })
            new PhoneModelWindow(_controller, id) { Owner = this }.ShowDialog();
    }

    private void OnDeleteScreenClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string id })
            _controller.RemoveScreen(id);
    }

    private void OnAddScreenWithPicker(object sender, RoutedEventArgs e)
    {
        var kind = SelectedAddKind();
        var orientation = AddLandscapeBtn.IsChecked == true
            ? SnugWin.Core.PhoneScreen.ScreenOrientation.Landscape
            : SnugWin.Core.PhoneScreen.ScreenOrientation.Portrait;
        if (kind == DeviceKind.Computer)
            orientation = SnugWin.Core.PhoneScreen.ScreenOrientation.Landscape;

        var picker = new PhoneModelWindow(
            _controller,
            screenId: null,
            allowedKinds: new HashSet<DeviceKind> { kind },
            selectionOnly: true,
            customTitle: $"选择{kind.DisplayName()}型号（{(orientation == SnugWin.Core.PhoneScreen.ScreenOrientation.Landscape ? "横屏" : "竖屏")}）")
        {
            Owner = this
        };

        if (picker.ShowDialog() == true && picker.SelectedChoice is { } choice)
        {
            string? customName = string.IsNullOrWhiteSpace(AddScreenNameBox.Text) ? null : AddScreenNameBox.Text.Trim();
            if (_controller.AddScreen(orientation, choice, customName))
                AddScreenNameBox.Clear();
        }
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo { FileName = _controller.LogDirectory, UseShellExecute = true });

    private async void OnInstallDriver(object sender, RoutedEventArgs e)
    {
        InstallDriverBtn.IsEnabled = false;
        InstallDriverBtn.Content = "安装中...";
        DriverWarningText.Text = "正在进行 SHA256 校验、导入证书并静默安装，请稍候...";
        try
        {
            var (ready, message) = await _controller.InstallDriverAsync();
            if (!ready)
                DriverWarningText.Text = message;
            Refresh();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"启动驱动安装程序失败：{ex.Message}", "SnugWin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            InstallDriverBtn.IsEnabled = true;
            InstallDriverBtn.Content = _controller.BundledDriverInstallerScript is not null ? "一键安装" : "下载驱动";
        }
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStateText.Text = "正在向 GitHub 查询最新版本...";
        try
        {
            var res = await _controller.CheckForUpdateAsync();
            RefreshUpdateUi(res);
        }
        catch (Exception ex)
        {
            UpdateStateText.Text = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        var upd = _controller.LatestUpdate;
        if (upd is null || !upd.HasUpdate || string.IsNullOrWhiteSpace(upd.PackageDownloadUrl))
            return;

        _isUpdating = true;
        CheckUpdateBtn.IsEnabled = false;
        InstallUpdateBtn.IsEnabled = false;
        UpdateProgressBar.Value = 0;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateStateText.Text = $"正在下载 v{upd.LatestVersion} ({upd.PackageAssetName})...";

        try
        {
            var progress = new Progress<double>(ratio =>
            {
                UpdateProgressBar.Value = ratio;
                UpdateStateText.Text = $"正在下载 v{upd.LatestVersion}... {ratio * 100:F0}%";
            });
            var prepared = await _controller.DownloadAndPrepareUpdateAsync(upd, progress, CancellationToken.None);
            UpdateStateText.Text = "校验通过，正在还原窗口并重启更新...";
            await _controller.ApplyPreparedUpdateAndRestartAsync(prepared);
        }
        catch (Exception ex)
        {
            _isUpdating = false;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            CheckUpdateBtn.IsEnabled = true;
            InstallUpdateBtn.IsEnabled = true;
            UpdateStateText.Text = $"更新失败：{ex.Message}";
            System.Windows.MessageBox.Show($"下载或校验更新失败：\n{ex.Message}", "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenRepo(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/nicocart/snug-win",
            UseShellExecute = true
        });
    }

    private async void OnRestartElevated(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            await _controller.ShutdownAsync();
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"提权重启取消或失败：{ex.Message}", "SnugWin", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
