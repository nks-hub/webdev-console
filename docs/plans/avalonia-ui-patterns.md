# Avalonia UI Patterns — DevForge

**Stack:** Avalonia 12.x · FluentTheme · CommunityToolkit.Mvvm · LiveCharts2 · gRPC  
**Date:** 2026-04-09

---

## 1. App.axaml — Application Setup

```xml
<!-- App.axaml -->
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="DevForge.GUI.App"
             RequestedThemeVariant="Dark">

  <Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://DevForge.GUI/Styles/DevForgeTokens.axaml" />
  </Application.Styles>

  <TrayIcon.Icons>
    <TrayIcons>
      <TrayIcon Icon="/Assets/tray.ico" ToolTipText="DevForge">
        <TrayIcon.Menu>
          <NativeMenu>
            <NativeMenuItem Header="Open DevForge" Command="{Binding ShowWindowCommand}" />
            <NativeMenuItemSeparator />
            <NativeMenuItem Header="Start All Services" Command="{Binding StartAllCommand}" />
            <NativeMenuItem Header="Stop All Services"  Command="{Binding StopAllCommand}" />
            <NativeMenuItemSeparator />
            <NativeMenuItem Header="Recent Sites">
              <!-- Populated dynamically at runtime via NativeMenu.Items binding -->
            </NativeMenuItem>
            <NativeMenuItemSeparator />
            <NativeMenuItem Header="Quit" Command="{Binding QuitCommand}" />
          </NativeMenu>
        </TrayIcon.Menu>
      </TrayIcon>
    </TrayIcons>
  </TrayIcon.Icons>

</Application>
```

```csharp
// App.axaml.cs
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new AppViewModel();
            DataContext = vm;
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Called from SettingsViewModel when theme toggle changes
    public static void ApplyTheme(bool isDark)
    {
        Current!.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        // Re-configure LiveCharts theme to match
        LiveCharts.Configure(cfg =>
        {
            if (isDark) cfg.AddDarkTheme(); else cfg.AddLightTheme();
        });
    }
}
```

---

## 2. MainWindow Layout

```xml
<!-- MainWindow.axaml -->
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:DevForge.GUI.ViewModels"
        x:Class="DevForge.GUI.Views.MainWindow"
        x:DataType="vm:MainWindowViewModel"
        Title="DevForge" Width="1100" Height="720"
        MinWidth="900" MinHeight="600"
        ExtendClientAreaToDecorationsHint="True"
        ExtendClientAreaChromeHints="NoChrome"
        SystemDecorations="BorderOnly">

  <Grid RowDefinitions="32,*,28">

    <!-- Custom title bar (draggable) -->
    <Grid Grid.Row="0" Background="{DynamicResource DevForgeSidebarBrush}"
          IsHitTestVisible="True">
      <Panel IsHitTestVisible="True">
        <!-- DragWindowGesture lets the user drag from this region -->
        <Interaction.Behaviors>
          <EventTriggerBehavior EventName="PointerPressed">
            <InvokeCommandAction Command="{Binding BeginDragCommand}" />
          </EventTriggerBehavior>
        </Interaction.Behaviors>
        <TextBlock Text="DevForge" VerticalAlignment="Center" Margin="16,0,0,0"
                   FontFamily="{StaticResource InterFont}" FontWeight="SemiBold" />
      </Panel>
      <!-- Window buttons (min/max/close) rendered by OS on macOS, manual on Windows -->
      <StackPanel HorizontalAlignment="Right" Orientation="Horizontal" Spacing="0">
        <Button Classes="titlebar-btn" Command="{Binding MinimizeCommand}" Content="─" />
        <Button Classes="titlebar-btn" Command="{Binding CloseCommand}" Content="✕" />
      </StackPanel>
    </Grid>

    <!-- Main content: sidebar + page area -->
    <Grid Grid.Row="1" ColumnDefinitions="200,*">

      <!-- Left sidebar navigation -->
      <StackPanel Grid.Column="0" Background="{DynamicResource DevForgeSidebarBrush}"
                  Spacing="2" Margin="8">
        <Button Classes="nav-item" Command="{Binding NavigateCommand}"
                CommandParameter="Dashboard" Content="Dashboard" />
        <Button Classes="nav-item" Command="{Binding NavigateCommand}"
                CommandParameter="Sites" Content="Sites" />
        <Button Classes="nav-item" Command="{Binding NavigateCommand}"
                CommandParameter="Settings" Content="Settings" />
      </StackPanel>

      <!-- Page content area (TransitioningContentControl for animated switching) -->
      <TransitioningContentControl Grid.Column="1"
                                   Content="{Binding CurrentPage}"
                                   PageTransition="{x:Null}" />
    </Grid>

    <!-- Status bar -->
    <Grid Grid.Row="2" Background="{DynamicResource DevForgeStatusBarBrush}"
          ColumnDefinitions="Auto,*,Auto">
      <Ellipse Grid.Column="0" Width="8" Height="8" Margin="12,0,6,0"
               Fill="{Binding DaemonStatusColor}" />
      <TextBlock Grid.Column="1" Text="{Binding DaemonStatusText}"
                 VerticalAlignment="Center" FontSize="11" />
      <TextBlock Grid.Column="2" Text="{Binding ActiveSiteCount, StringFormat='{}{0} sites'}"
                 VerticalAlignment="Center" FontSize="11" Margin="0,0,12,0" />
    </Grid>

  </Grid>
</Window>
```

```csharp
// MainWindowViewModel.cs
public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _daemonStatusText = "Connecting…";
    [ObservableProperty] private IBrush _daemonStatusColor = Brushes.Yellow;
    [ObservableProperty] private int _activeSiteCount;

    private readonly Dictionary<string, object> _pages;

    public MainWindowViewModel(IDaemonClient daemon)
    {
        _pages = new()
        {
            ["Dashboard"] = new DashboardViewModel(daemon),
            ["Sites"]     = new SitesViewModel(daemon),
            ["Settings"]  = new SettingsViewModel(),
        };
        CurrentPage = _pages["Dashboard"];
    }

    [RelayCommand]
    private void Navigate(string pageName)
    {
        if (_pages.TryGetValue(pageName, out var page))
            CurrentPage = page;
    }

    [RelayCommand]
    private void BeginDrag()
    {
        // Must be called from code-behind where Window reference is available
    }
}
```

---

## 3. ServiceCard — Custom UserControl

```xml
<!-- Controls/ServiceCard.axaml -->
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:DevForge.GUI.ViewModels"
             xmlns:lvc="using:LiveChartsCore.SkiaSharpView.Avalonia"
             x:Class="DevForge.GUI.Controls.ServiceCard"
             x:DataType="vm:ServiceViewModel">

  <Border Classes="card" CornerRadius="8" Padding="14" Width="220">
    <StackPanel Spacing="10">

      <!-- Header: icon + name + status dot -->
      <Grid ColumnDefinitions="32,*,12">
        <Image Grid.Column="0" Source="{Binding IconPath}" Width="24" Height="24" />
        <TextBlock Grid.Column="1" Text="{Binding Name}"
                   FontWeight="SemiBold" VerticalAlignment="Center" />
        <!-- Status dot: color driven by StatusBrush computed property -->
        <Ellipse Grid.Column="2" Width="10" Height="10"
                 Fill="{Binding StatusBrush}" VerticalAlignment="Center" />
      </Grid>

      <!-- CPU/RAM sparkline (LiveCharts2 mini chart) -->
      <lvc:CartesianChart Height="48"
                          Series="{Binding SparklineSeries}"
                          XAxes="{Binding HiddenXAxis}"
                          YAxes="{Binding HiddenYAxis}"
                          ZoomMode="None" />

      <!-- Metrics row -->
      <Grid ColumnDefinitions="*,*">
        <TextBlock Grid.Column="0" Text="{Binding CpuPercent, StringFormat='CPU {0:F0}%'}"
                   FontSize="11" Foreground="{DynamicResource DevForgeSubtleBrush}" />
        <TextBlock Grid.Column="1" Text="{Binding RamMb, StringFormat='RAM {0} MB'}"
                   FontSize="11" Foreground="{DynamicResource DevForgeSubtleBrush}"
                   HorizontalAlignment="Right" />
      </Grid>

      <!-- Action buttons -->
      <StackPanel Orientation="Horizontal" Spacing="6">
        <Button Classes="action-btn green" Content="Start"
                Command="{Binding StartCommand}"
                IsVisible="{Binding !IsRunning}" />
        <Button Classes="action-btn red" Content="Stop"
                Command="{Binding StopCommand}"
                IsVisible="{Binding IsRunning}" />
        <Button Classes="action-btn" Content="Restart"
                Command="{Binding RestartCommand}"
                IsEnabled="{Binding IsRunning}" />
        <!-- Spinner shown while command is in-flight -->
        <ProgressBar Classes="spinner" IsVisible="{Binding IsBusy}"
                     IsIndeterminate="True" Width="16" Height="16" />
      </StackPanel>

    </StackPanel>
  </Border>
</UserControl>
```

```csharp
// ViewModels/ServiceViewModel.cs
public partial class ServiceViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ServiceStatus _status;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private int _ramMb;
    [ObservableProperty] private bool _isBusy;

    // Sparkline — fixed 60-point circular buffer
    public ObservableCollection<ObservableValue> SparklineValues { get; } = new();
    public ISeries[] SparklineSeries { get; }
    public ICartesianAxis[] HiddenXAxis { get; } = [new Axis { IsVisible = false }];
    public ICartesianAxis[] HiddenYAxis { get; } = [new Axis { IsVisible = false, MinLimit = 0, MaxLimit = 100 }];

    public bool IsRunning => Status == ServiceStatus.Running;

    // Computed brush — no code-behind required; OnStatusChanged triggers refresh
    public IBrush StatusBrush => Status switch
    {
        ServiceStatus.Running => new SolidColorBrush(Color.Parse("#22c55e")),
        ServiceStatus.Stopped => new SolidColorBrush(Color.Parse("#ef4444")),
        _                     => new SolidColorBrush(Color.Parse("#eab308")),
    };

    private readonly IDaemonClient _daemon;

    public ServiceViewModel(string name, IDaemonClient daemon)
    {
        _name = name;
        _daemon = daemon;
        SparklineSeries =
        [
            new LineSeries<ObservableValue>
            {
                Values = SparklineValues,
                GeometrySize = 0,
                LineSmoothness = 0.5,
                Stroke = new SolidColorPaint(SKColors.CornflowerBlue, 1.5f),
                Fill = null,
            }
        ];
    }

    partial void OnStatusChanged(ServiceStatus value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusBrush));
    }

    public void PushMetrics(double cpu, int ram)
    {
        CpuPercent = cpu;
        RamMb = ram;
        if (SparklineValues.Count >= 60) SparklineValues.RemoveAt(0);
        SparklineValues.Add(new ObservableValue(cpu));
    }

    [IAsyncRelayCommand]
    private async Task StartAsync(CancellationToken ct)
    {
        IsBusy = true;
        try { await _daemon.StartServiceAsync(Name, ct); }
        finally { IsBusy = false; }
    }

    [IAsyncRelayCommand]
    private async Task StopAsync(CancellationToken ct)
    {
        IsBusy = true;
        try { await _daemon.StopServiceAsync(Name, ct); }
        finally { IsBusy = false; }
    }

    [IAsyncRelayCommand]
    private async Task RestartAsync(CancellationToken ct)
    {
        IsBusy = true;
        try { await _daemon.RestartServiceAsync(Name, ct); }
        finally { IsBusy = false; }
    }
}
```

---

## 4. SiteCard Control

```xml
<!-- Controls/SiteCard.axaml -->
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:DevForge.GUI.ViewModels"
             x:Class="DevForge.GUI.Controls.SiteCard"
             x:DataType="vm:SiteViewModel">

  <Border Classes="card" CornerRadius="8" Padding="14" Width="280">
    <StackPanel Spacing="8">

      <!-- Domain + SSL lock -->
      <StackPanel Orientation="Horizontal" Spacing="6">
        <TextBlock Text="{Binding SslIcon}"   FontSize="14" VerticalAlignment="Center"
                   Foreground="{Binding SslBrush}" />
        <!-- Clickable domain opens browser -->
        <Button Classes="link" Content="{Binding Domain}"
                Command="{Binding OpenInBrowserCommand}" />
      </StackPanel>

      <!-- Document root (truncated with tooltip) -->
      <TextBlock Text="{Binding DocRoot}" FontFamily="{StaticResource MonoFont}"
                 FontSize="11" TextTrimming="CharacterEllipsis" MaxWidth="240"
                 ToolTip.Tip="{Binding DocRoot}"
                 Foreground="{DynamicResource DevForgeSubtleBrush}" />

      <!-- PHP version badge -->
      <Border CornerRadius="4" Padding="6,2"
              Background="{Binding PhpVersionBadgeBrush}" HorizontalAlignment="Left">
        <TextBlock Text="{Binding PhpVersion, StringFormat='PHP {0}'}"
                   FontSize="11" FontWeight="SemiBold" />
      </Border>

      <!-- Quick actions -->
      <StackPanel Orientation="Horizontal" Spacing="6" Margin="0,4,0,0">
        <Button Classes="icon-btn" Content="✎" ToolTip.Tip="Edit site"
                Command="{Binding EditCommand}" />
        <Button Classes="icon-btn" Content="⌨" ToolTip.Tip="Open terminal"
                Command="{Binding OpenTerminalCommand}" />
        <Button Classes="icon-btn danger" Content="🗑" ToolTip.Tip="Delete site"
                Command="{Binding DeleteCommand}" />
      </StackPanel>

    </StackPanel>
  </Border>
</UserControl>
```

```csharp
// ViewModels/SiteViewModel.cs
public partial class SiteViewModel : ObservableObject
{
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _docRoot = "";
    [ObservableProperty] private string _phpVersion = "";
    [ObservableProperty] private bool _hasSsl;

    public string SslIcon  => HasSsl ? "🔒" : "🔓";
    public IBrush SslBrush => HasSsl
        ? new SolidColorBrush(Color.Parse("#22c55e"))
        : new SolidColorBrush(Color.Parse("#6b7280"));

    // Badge color keyed by PHP major.minor
    public IBrush PhpVersionBadgeBrush => PhpVersion switch
    {
        var v when v.StartsWith("8.4") => new SolidColorBrush(Color.Parse("#6d28d9")),
        var v when v.StartsWith("8.3") => new SolidColorBrush(Color.Parse("#2563eb")),
        var v when v.StartsWith("8.2") => new SolidColorBrush(Color.Parse("#0891b2")),
        _                              => new SolidColorBrush(Color.Parse("#374151")),
    };

    [RelayCommand]
    private void OpenInBrowser() => Process.Start(new ProcessStartInfo
    {
        FileName = (HasSsl ? "https://" : "http://") + Domain,
        UseShellExecute = true,
    });

    [RelayCommand] private void Edit() { /* open EditSiteDialog */ }
    [RelayCommand] private void OpenTerminal() { /* spawn terminal at DocRoot */ }

    [IAsyncRelayCommand]
    private async Task DeleteAsync(CancellationToken ct)
    {
        // TODO: confirmation dialog before delete
    }
}
```

---

## 5. Dashboard Page

```xml
<!-- Views/DashboardPage.axaml -->
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:DevForge.GUI.ViewModels"
             xmlns:ctrl="using:DevForge.GUI.Controls"
             xmlns:lvc="using:LiveChartsCore.SkiaSharpView.Avalonia"
             x:Class="DevForge.GUI.Views.DashboardPage"
             x:DataType="vm:DashboardViewModel">

  <ScrollViewer>
    <StackPanel Spacing="20" Margin="16">

      <!-- Service cards row -->
      <ItemsControl ItemsSource="{Binding Services}">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate>
            <WrapPanel Orientation="Horizontal" />
          </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate DataType="vm:ServiceViewModel">
            <ctrl:ServiceCard DataContext="{Binding}" Margin="0,0,12,12" />
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>

      <!-- CPU/RAM time-series chart -->
      <Border Classes="card" Padding="16" CornerRadius="8">
        <StackPanel Spacing="8">
          <TextBlock Text="System Resources" FontWeight="SemiBold" />
          <lvc:CartesianChart Height="160"
                              Series="{Binding ResourceSeries}"
                              XAxes="{Binding TimeAxis}"
                              YAxes="{Binding PercentAxis}"
                              ZoomMode="None" />
        </StackPanel>
      </Border>

      <!-- Activity log — virtualized with ItemsRepeater -->
      <Border Classes="card" Padding="16" CornerRadius="8">
        <StackPanel Spacing="8">
          <TextBlock Text="Activity Log" FontWeight="SemiBold" />
          <ScrollViewer MaxHeight="200">
            <ItemsRepeater ItemsSource="{Binding LogEntries}">
              <ItemsRepeater.ItemTemplate>
                <DataTemplate DataType="vm:LogEntryViewModel">
                  <Grid ColumnDefinitions="130,*" Margin="0,2">
                    <TextBlock Grid.Column="0" Text="{Binding Timestamp, StringFormat='{}{0:HH:mm:ss}'}"
                               FontFamily="{StaticResource MonoFont}" FontSize="11"
                               Foreground="{DynamicResource DevForgeSubtleBrush}" />
                    <TextBlock Grid.Column="1" Text="{Binding Message}" FontSize="11" />
                  </Grid>
                </DataTemplate>
              </ItemsRepeater.ItemTemplate>
            </ItemsRepeater>
          </ScrollViewer>
        </StackPanel>
      </Border>

    </StackPanel>
  </ScrollViewer>
</UserControl>
```

---

## 6. Sites Page

```xml
<!-- Views/SitesPage.axaml -->
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:DevForge.GUI.ViewModels"
             xmlns:ctrl="using:DevForge.GUI.Controls"
             x:Class="DevForge.GUI.Views.SitesPage"
             x:DataType="vm:SitesViewModel">

  <Grid RowDefinitions="Auto,*">

    <!-- Toolbar -->
    <Grid Grid.Row="0" ColumnDefinitions="*,Auto" Margin="16,12">
      <TextBox Grid.Column="0" Watermark="Search sites…"
               Text="{Binding SearchText}" MaxWidth="320" />
      <Button Grid.Column="1" Content="+ New Site"
              Command="{Binding OpenCreateWizardCommand}" Classes="primary" />
    </Grid>

    <!-- Site cards or empty state -->
    <Panel Grid.Row="1">

      <!-- Empty state -->
      <StackPanel IsVisible="{Binding !HasSites}"
                  HorizontalAlignment="Center" VerticalAlignment="Center" Spacing="12">
        <TextBlock Text="No sites yet" FontSize="18" FontWeight="SemiBold"
                   HorizontalAlignment="Center" />
        <TextBlock Text="Create your first site to get started."
                   Foreground="{DynamicResource DevForgeSubtleBrush}"
                   HorizontalAlignment="Center" />
        <Button Content="+ New Site" Command="{Binding OpenCreateWizardCommand}"
                Classes="primary" HorizontalAlignment="Center" />
      </StackPanel>

      <!-- Site cards (wrap layout, virtualized) -->
      <ScrollViewer IsVisible="{Binding HasSites}">
        <ItemsRepeater ItemsSource="{Binding FilteredSites}" Margin="16,0">
          <ItemsRepeater.Layout>
            <WrapLayout Orientation="Horizontal" HorizontalSpacing="12" VerticalSpacing="12" />
          </ItemsRepeater.Layout>
          <ItemsRepeater.ItemTemplate>
            <DataTemplate DataType="vm:SiteViewModel">
              <ctrl:SiteCard DataContext="{Binding}" />
            </DataTemplate>
          </ItemsRepeater.ItemTemplate>
        </ItemsRepeater>
      </ScrollViewer>

    </Panel>
  </Grid>
</UserControl>
```

```csharp
// ViewModels/SitesViewModel.cs
public partial class SitesViewModel : ObservableObject
{
    [ObservableProperty] private string _searchText = "";
    private readonly ObservableCollection<SiteViewModel> _allSites = new();

    // ReadOnlyObservableCollection filtered by SearchText
    public ReadOnlyObservableCollection<SiteViewModel> FilteredSites { get; }

    public bool HasSites => FilteredSites.Count > 0;

    partial void OnSearchTextChanged(string value)
    {
        // Trigger re-filter; FilteredSites is backed by a DynamicData or manual filter
        OnPropertyChanged(nameof(FilteredSites));
        OnPropertyChanged(nameof(HasSites));
    }

    [RelayCommand]
    private async Task OpenCreateWizardAsync()
    {
        var result = await CreateSiteDialog.ShowAsync();
        if (result is not null) _allSites.Add(new SiteViewModel(result));
    }
}
```

---

## 7. Create Site Wizard

```csharp
// ViewModels/CreateSiteWizardViewModel.cs
public partial class CreateSiteWizardViewModel : ObservableObject
{
    [ObservableProperty] private int _currentStep = 1;
    public int TotalSteps => 4;

    // Step 1
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _docRoot = "";

    // Step 2
    [ObservableProperty] private string _selectedPhpVersion = "8.3";
    public IReadOnlyList<string> PhpVersions { get; } = ["8.4", "8.3", "8.2", "8.1"];

    // Step 3
    [ObservableProperty] private bool _enableSsl = true;
    [ObservableProperty] private string _webServer = "apache";
    public IReadOnlyList<string> WebServers { get; } = ["apache", "nginx"];

    // Step 4
    [ObservableProperty] private bool _createDatabase;
    [ObservableProperty] private string _databaseName = "";

    public bool CanGoNext => CurrentStep < TotalSteps && IsCurrentStepValid();
    public bool CanGoBack => CurrentStep > 1;
    public bool IsLastStep => CurrentStep == TotalSteps;

    private bool IsCurrentStepValid() => CurrentStep switch
    {
        1 => !string.IsNullOrWhiteSpace(Domain) && !string.IsNullOrWhiteSpace(DocRoot),
        2 => !string.IsNullOrWhiteSpace(SelectedPhpVersion),
        3 => true,
        4 => !CreateDatabase || !string.IsNullOrWhiteSpace(DatabaseName),
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() { CurrentStep++; RefreshCanExecute(); }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() { CurrentStep--; RefreshCanExecute(); }

    [IAsyncRelayCommand]
    private async Task CreateAsync(CancellationToken ct)
    {
        // Call daemon gRPC: CreateSite with collected data
    }

    private void RefreshCanExecute()
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLastStep));
    }

    [RelayCommand]
    private async Task BrowseDocRootAsync()
    {
        var picker = new OpenFolderDialog();
        var result = await picker.ShowAsync(App.MainWindow);
        if (result is not null) DocRoot = result;
    }
}
```

The wizard AXAML uses a `Carousel` or `TransitioningContentControl` bound to `CurrentStep` with individual step `UserControl` children. Navigation buttons are wired to `NextCommand` / `BackCommand`.

---

## 8. Settings Page

```xml
<!-- Views/SettingsPage.axaml (excerpt — port + theme sections) -->
<ScrollViewer>
  <StackPanel Spacing="24" Margin="24" MaxWidth="560">

    <!-- Ports section -->
    <StackPanel Spacing="12">
      <TextBlock Text="Ports" FontWeight="SemiBold" FontSize="14" />
      <Grid ColumnDefinitions="*,120" RowDefinitions="Auto,Auto,Auto" RowSpacing="8">
        <TextBlock Grid.Row="0" Grid.Column="0" Text="HTTP port" VerticalAlignment="Center" />
        <TextBox   Grid.Row="0" Grid.Column="1" Text="{Binding HttpPort}"
                   Classes="{Binding HttpPortError, Converter={StaticResource ErrorClassConverter}}" />

        <TextBlock Grid.Row="1" Grid.Column="0" Text="HTTPS port" VerticalAlignment="Center" />
        <TextBox   Grid.Row="1" Grid.Column="1" Text="{Binding HttpsPort}" />

        <TextBlock Grid.Row="2" Grid.Column="0" Text="MySQL port" VerticalAlignment="Center" />
        <TextBox   Grid.Row="2" Grid.Column="1" Text="{Binding MysqlPort}" />
      </Grid>
    </StackPanel>

    <!-- Theme section -->
    <StackPanel Spacing="12">
      <TextBlock Text="Appearance" FontWeight="SemiBold" FontSize="14" />
      <ComboBox ItemsSource="{Binding ThemeOptions}"
                SelectedItem="{Binding SelectedTheme}"
                Width="200" />
    </StackPanel>

    <Button Content="Save Settings" Command="{Binding SaveCommand}" Classes="primary" />

  </StackPanel>
</ScrollViewer>
```

```csharp
// ViewModels/SettingsViewModel.cs
public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HttpPortError))]
    private string _httpPort = "80";

    public string? HttpPortError => ValidatePort(HttpPort);

    private static string? ValidatePort(string value) =>
        int.TryParse(value, out int p) && p is >= 1 and <= 65535
            ? null
            : "Port must be 1–65535";

    public IReadOnlyList<string> ThemeOptions { get; } = ["Dark", "Light", "System"];
    [ObservableProperty] private string _selectedTheme = "Dark";

    partial void OnSelectedThemeChanged(string value)
    {
        App.ApplyTheme(value switch
        {
            "Dark"  => true,
            "Light" => false,
            _       => !App.Current!.PlatformSettings!.GetColorValues().ThemeVariant
                            .Equals(PlatformThemeVariant.Light),
        });
    }

    [IAsyncRelayCommand]
    private async Task SaveAsync(CancellationToken ct) { /* persist via gRPC */ }
}
```

---

## 9. MVVM Patterns

### ObservableProperty + RelayCommand

```csharp
// CommunityToolkit.Mvvm generates backing field + INotifyPropertyChanged plumbing
[ObservableProperty]
private bool _isLoading;

// IAsyncRelayCommand auto-manages IsBusy, CanExecute(false) while running
[IAsyncRelayCommand]
private async Task LoadDataAsync(CancellationToken ct)
{
    IsLoading = true;
    try
    {
        var resp = await _daemon.ListServicesAsync(ct);
        Services.Clear();
        foreach (var s in resp.Services)
            Services.Add(new ServiceViewModel(s.Name, _daemon));
    }
    finally { IsLoading = false; }
}
```

### ObservableCollection for service list

```csharp
public ObservableCollection<ServiceViewModel> Services { get; } = new();
```

Always update `ObservableCollection` on the UI thread. From background threads, use the marshaling pattern shown in section 11.

---

## 10. Design Tokens — Resource Dictionary

```xml
<!-- Styles/DevForgeTokens.axaml -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <!-- Color primitives -->
  <Color x:Key="DevForgeAccent">#6366f1</Color>
  <Color x:Key="DevForgeSurface">#1e1e2e</Color>
  <Color x:Key="DevForgeSidebar">#181825</Color>
  <Color x:Key="DevForgeStatusBar">#11111b</Color>
  <Color x:Key="DevForgeSubtle">#6c7086</Color>
  <Color x:Key="DevForgeSuccess">#22c55e</Color>
  <Color x:Key="DevForgeDanger">#ef4444</Color>
  <Color x:Key="DevForgeWarning">#eab308</Color>

  <!-- Brushes -->
  <SolidColorBrush x:Key="DevForgeAccentBrush"    Color="{StaticResource DevForgeAccent}" />
  <SolidColorBrush x:Key="DevForgeSidebarBrush"   Color="{StaticResource DevForgeSidebar}" />
  <SolidColorBrush x:Key="DevForgeStatusBarBrush" Color="{StaticResource DevForgeStatusBar}" />
  <SolidColorBrush x:Key="DevForgeSubtleBrush"    Color="{StaticResource DevForgeSubtle}" />

  <!-- Typography -->
  <FontFamily x:Key="InterFont">avares://DevForge.GUI/Assets/Fonts/Inter#Inter</FontFamily>
  <FontFamily x:Key="MonoFont">avares://DevForge.GUI/Assets/Fonts/JetBrainsMono#JetBrains Mono</FontFamily>

  <!-- Card style -->
  <Style Selector="Border.card">
    <Setter Property="Background"  Value="{DynamicResource ControlFillColorDefaultBrush}" />
    <Setter Property="BorderBrush" Value="{DynamicResource ControlStrokeColorDefaultBrush}" />
    <Setter Property="BorderThickness" Value="1" />
  </Style>

  <!-- Primary button style -->
  <Style Selector="Button.primary">
    <Setter Property="Background" Value="{StaticResource DevForgeAccentBrush}" />
    <Setter Property="Foreground" Value="White" />
    <Setter Property="CornerRadius" Value="6" />
  </Style>

  <!-- Navigation item -->
  <Style Selector="Button.nav-item">
    <Setter Property="Background"   Value="Transparent" />
    <Setter Property="Foreground"   Value="{DynamicResource TextFillColorPrimaryBrush}" />
    <Setter Property="HorizontalContentAlignment" Value="Left" />
    <Setter Property="Width"        Value="184" />
    <Setter Property="CornerRadius" Value="6" />
  </Style>
  <Style Selector="Button.nav-item:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
  </Style>

</ResourceDictionary>
```

---

## 11. gRPC Streaming → UI Updates

```csharp
// DashboardViewModel.cs — subscribes to daemon metrics stream
public partial class DashboardViewModel : ObservableObject, IAsyncDisposable
{
    private CancellationTokenSource _cts = new();

    // Throttle: max 30 UI updates per second (≈33ms min interval)
    private DateTime _lastChartUpdate = DateTime.MinValue;
    private const int ThrottleMs = 33;

    public async Task StartStreamingAsync()
    {
        using var call = _daemon.StreamMetrics(new MetricsRequest(), cancellationToken: _cts.Token);

        await foreach (var frame in call.ResponseStream.ReadAllAsync(_cts.Token))
        {
            // Background thread — must marshal to UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Update service metrics (always — fast operation)
                var svc = Services.FirstOrDefault(s => s.Name == frame.ServiceName);
                svc?.PushMetrics(frame.CpuPercent, frame.RamMb);

                // Throttle chart redraws to 30 FPS
                var now = DateTime.UtcNow;
                if ((now - _lastChartUpdate).TotalMilliseconds < ThrottleMs) return;
                _lastChartUpdate = now;

                if (ResourceSeries is [LineSeries<ObservableValue> cpu, ..])
                {
                    if (cpu.Values is ObservableCollection<ObservableValue> cpuVals)
                    {
                        if (cpuVals.Count >= 120) cpuVals.RemoveAt(0);
                        cpuVals.Add(new ObservableValue(frame.TotalCpuPercent));
                    }
                }
            }, DispatcherPriority.Render);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
```

Key rules:
- `Dispatcher.UIThread.InvokeAsync` with `DispatcherPriority.Render` batches updates with the render pass.
- `_cts.CancelAsync()` (not `Cancel()`) is the non-blocking .NET 8+ API.
- The throttle gate `ThrottleMs = 33` caps chart redraws at ~30 FPS regardless of stream frequency.
- `ObservableCollection` mutations are safe inside `UIThread.InvokeAsync`; do not mutate from background threads directly.
- On reconnect after daemon restart, re-create the `CancellationTokenSource` and call `StartStreamingAsync` again from the connection-restored event.

---

## Summary: Key Avalonia 12 Gotchas

| Area | Gotcha | Fix |
|---|---|---|
| Theme toggle | `FluentTheme.Mode` removed | Use `Application.RequestedThemeVariant` |
| TrayIcon | Cannot be in Window | Declare in `App.axaml` |
| Compiled bindings | `{Binding}` still works but is reflection-based | Use `x:DataType` on all root elements |
| LiveCharts2 | Avalonia 12 compat unconfirmed in 2.0.0 stable | Test early; fallback to OxyPlot |
| UI thread | gRPC streams arrive on thread pool | Always use `Dispatcher.UIThread.InvokeAsync` |
| `ObservableProperty` notify | Property changed for derived props not auto-generated | Add `[NotifyPropertyChangedFor(nameof(...))]` |
| Font embedding | Fonts must be `AvaloniaResource` build action | Reference via `avares://` URI in AXAML |
