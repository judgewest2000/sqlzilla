using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sqlzilla.Models;
using Sqlzilla.Services;

namespace Sqlzilla.Views;

/// <summary>Code-built ContentDialogs used across the app.</summary>
public static class Dialogs
{
    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private static ContentDialog Create(XamlRoot root, string title, object content, string primary, string? secondary = null) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        SecondaryButtonText = secondary ?? "",
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
    };

    /// <summary>WinUI allows one ContentDialog at a time; dialogs that can pop up unprompted (updates, files opened from Explorer) wait their turn.</summary>
    public static async Task WhenNoDialogOpenAsync(XamlRoot root)
    {
        while (VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(p => p.Child is ContentDialog))
            await Task.Delay(250);
    }

    public static async Task ShowErrorAsync(XamlRoot root, string title, string message)
    {
        await WhenNoDialogOpenAsync(root);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string confirm = "Delete")
    {
        await WhenNoDialogOpenAsync(root);
        var dialog = Create(root, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, confirm);
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Returns Primary = save, Secondary = discard, None = cancel.</summary>
    public static async Task<ContentDialogResult> ConfirmUnsavedAsync(XamlRoot root, string name)
    {
        var dialog = Create(root, "Unsaved changes",
            new TextBlock { Text = $"Save changes to “{name}” before closing?", TextWrapping = TextWrapping.Wrap },
            "Save", "Don't save");
        return await dialog.ShowAsync();
    }

    // ------------------------------------------------------------------ groups

    /// <summary>Edits a group's name, colour and read-only flag in place. Returns false if cancelled.</summary>
    public static async Task<bool> EditGroupAsync(XamlRoot root, ServerGroup group, bool isNew)
    {
        var nameBox = new TextBox { Header = "Name", Text = group.Name, PlaceholderText = "e.g. Production" };
        var color = GroupColors.Parse(group.Color);

        var preview = new Border { Height = 36, CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 0, 10, 0) };
        var previewText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        preview.Child = previewText;

        var picker = new ColorPicker
        {
            Color = color,
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            Visibility = Visibility.Collapsed,
        };

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void UpdatePreview()
        {
            preview.Background = new SolidColorBrush(color);
            previewText.Foreground = new SolidColorBrush(GroupColors.ContrastText(color));
            previewText.Text = $"{(nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : "Group")} · query windows use this colour";
            foreach (var b in swatches.Children.OfType<Button>())
                b.BorderBrush = new SolidColorBrush(GroupColors.ToHex(color) == (string)b.Tag ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Transparent);
        }
        foreach (var hex in GroupColors.Presets)
        {
            var swatch = new Button
            {
                Width = 28, Height = 28, Padding = new Thickness(0), Tag = hex,
                Background = GroupColors.Brush(hex), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(14),
            };
            ToolTipService.SetToolTip(swatch, hex);
            swatch.Click += (_, _) => { color = GroupColors.Parse(hex); picker.Color = color; UpdatePreview(); };
            swatches.Children.Add(swatch);
        }
        var customButton = new HyperlinkButton { Content = "Custom colour…", Padding = new Thickness(4, 0, 4, 0) };
        customButton.Click += (_, _) => picker.Visibility = picker.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        swatches.Children.Add(customButton);
        picker.ColorChanged += (_, args) => { color = Windows.UI.Color.FromArgb(255, args.NewColor.R, args.NewColor.G, args.NewColor.B); UpdatePreview(); };
        nameBox.TextChanged += (_, _) => UpdatePreview();

        var readOnly = new ToggleSwitch
        {
            Header = "Read-only",
            IsOn = group.IsReadOnly,
            OnContent = "Connect with ApplicationIntent=ReadOnly and confirm before running writes",
            OffContent = "Off",
        };

        var panel = new StackPanel { Spacing = 12, Width = 440 };
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Colour" });
        panel.Children.Add(swatches);
        panel.Children.Add(picker);
        panel.Children.Add(preview);
        panel.Children.Add(readOnly);
        UpdatePreview();
        nameBox.Loaded += (_, _) => { nameBox.Focus(FocusState.Programmatic); nameBox.SelectAll(); };

        var dialog = Create(root, isNew ? "New group" : "Edit group", new ScrollViewer { Content = panel }, isNew ? "Create" : "Save");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        group.Name = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : "New Group";
        group.Color = GroupColors.ToHex(color);
        group.IsReadOnly = readOnly.IsOn;
        return true;
    }

    // ------------------------------------------------------------------ servers

    private static readonly AuthMode[] AuthModes = Enum.GetValues<AuthMode>();

    /// <summary>Edits a copy of <paramref name="server"/>. Returns the edited copy and chosen group, or null if cancelled.</summary>
    public static async Task<(ServerConnection Server, ServerGroup Group)?> EditServerAsync(
        XamlRoot root, ServerConnection server, IReadOnlyList<ServerGroup> groups, ServerGroup group, bool isNew)
    {
        var s = server.Clone();
        const double width = 500;

        var groupBox = new ComboBox { Header = "Group", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var g in groups)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            item.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = GroupColors.Brush(g.Color), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = g.LabelWithReadOnly });
            groupBox.Items.Add(item);
        }
        groupBox.SelectedIndex = Math.Max(0, groups.ToList().IndexOf(group));

        var nameBox = new TextBox { Header = "Display name (optional)", Text = s.Name, PlaceholderText = "Defaults to the server name" };
        var serverBox = new TextBox { Header = "Server", Text = s.Server, PlaceholderText = "myserver.database.windows.net, host\\instance or host,port" };
        var authBox = new ComboBox { Header = "Authentication", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var mode in AuthModes) authBox.Items.Add(ServerConnection.AuthLabel(mode));
        authBox.SelectedIndex = Array.IndexOf(AuthModes, s.AuthMode);
        var userBox = new TextBox { Header = "User name", Text = s.UserName };
        var passwordBox = new PasswordBox { Header = "Password", Password = s.Password };
        var connectionStringBox = new TextBox
        {
            Header = "Connection string",
            Text = s.ConnectionString,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            FontFamily = (FontFamily)Application.Current.Resources["CodeFont"],
            IsSpellCheckEnabled = false,
            PlaceholderText = "Server=…;Database=…;Authentication=Active Directory Interactive;",
        };
        var databaseBox = new ComboBox
        {
            Header = "Database (optional — leave empty to browse all databases on the server)",
            IsEditable = true,
            Text = s.Database,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "<all databases>",
        };
        var encryptBox = new ComboBox { Header = "Encrypt", MinWidth = 150 };
        foreach (var e in Enum.GetValues<EncryptMode>()) encryptBox.Items.Add(e.ToString());
        encryptBox.SelectedIndex = (int)s.Encrypt;
        var trustBox = new CheckBox { Content = "Trust server certificate", IsChecked = s.TrustServerCertificate, VerticalAlignment = VerticalAlignment.Bottom };
        var timeoutBox = new NumberBox
        {
            Header = "Timeout (s)", Value = s.ConnectTimeout, Minimum = 1, Maximum = 600,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 110,
        };
        var hint = new TextBlock { Foreground = Secondary, TextWrapping = TextWrapping.Wrap, FontSize = 12 };

        var options = new Grid { ColumnSpacing = 12 };
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(timeoutBox, 1);
        Grid.SetColumn(trustBox, 2);
        options.Children.Add(encryptBox);
        options.Children.Add(timeoutBox);
        options.Children.Add(trustBox);

        var testButton = new Button { Content = "Test connection" };
        var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true };
        var testRow = new Grid { ColumnSpacing = 12 };
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(testResult, 1);
        testRow.Children.Add(testButton);
        testRow.Children.Add(testResult);

        AuthMode Mode() => authBox.SelectedIndex >= 0 ? AuthModes[authBox.SelectedIndex] : AuthMode.SqlLogin;

        void UpdateFields()
        {
            var mode = Mode();
            var raw = mode == AuthMode.ConnectionString;
            serverBox.Visibility = options.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
            connectionStringBox.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
            databaseBox.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
            userBox.Visibility = mode is AuthMode.SqlLogin or AuthMode.EntraInteractive or AuthMode.EntraPassword or AuthMode.EntraServicePrincipal
                ? Visibility.Visible : Visibility.Collapsed;
            passwordBox.Visibility = mode is AuthMode.SqlLogin or AuthMode.EntraPassword or AuthMode.EntraServicePrincipal
                ? Visibility.Visible : Visibility.Collapsed;
            userBox.Header = mode switch
            {
                AuthMode.EntraInteractive => "User name (optional sign-in hint)",
                AuthMode.EntraServicePrincipal => "Application (client) ID",
                AuthMode.EntraPassword => "User name (user@tenant.com)",
                _ => "Login",
            };
            passwordBox.Header = mode == AuthMode.EntraServicePrincipal ? "Client secret" : "Password";
            hint.Text = mode switch
            {
                AuthMode.EntraInteractive => "Signs in through the Windows account broker, with MFA if required. Tokens are cached for the session.",
                AuthMode.EntraDefault => "Uses the first available of: environment variables, managed identity, Visual Studio, Azure CLI (az login), Azure PowerShell, Azure Developer CLI.",
                AuthMode.EntraPassword => "Deprecated by Microsoft and doesn't support MFA; prefer interactive.",
                AuthMode.ConnectionString => "Any SqlClient connection string. Include Initial Catalog/Database to pin it to one database.",
                AuthMode.WindowsIntegrated => "Uses your Windows sign-in (Kerberos/NTLM).",
                _ => "",
            };
            hint.Visibility = hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        ServerConnection Read()
        {
            var r = s.Clone();
            r.Name = nameBox.Text.Trim();
            r.Server = serverBox.Text.Trim();
            r.AuthMode = Mode();
            r.UserName = userBox.Text.Trim();
            r.Password = passwordBox.Password;
            r.ConnectionString = connectionStringBox.Text.Trim();
            r.Database = (databaseBox.Text ?? "").Trim();
            r.Encrypt = (EncryptMode)Math.Max(0, encryptBox.SelectedIndex);
            r.TrustServerCertificate = trustBox.IsChecked == true;
            r.ConnectTimeout = double.IsNaN(timeoutBox.Value) ? 30 : (int)timeoutBox.Value;
            return r;
        }

        authBox.SelectionChanged += (_, _) => UpdateFields();
        CancellationTokenSource? testCts = null;
        testButton.Click += async (_, _) =>
        {
            testCts?.Cancel();
            var cts = testCts = new CancellationTokenSource();
            testButton.IsEnabled = false;
            testResult.Foreground = Secondary;
            testResult.Text = "Connecting…";
            try
            {
                var candidate = Read();
                var readOnly = groupBox.SelectedIndex >= 0 && groups[groupBox.SelectedIndex].IsReadOnly;
                testResult.Text = await Task.Run(() => ConnectionFactory.TestAsync(candidate, cts.Token));
                testResult.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                if (candidate.AuthMode != AuthMode.ConnectionString && databaseBox.Items.Count == 0)
                {
                    var databases = await Task.Run(() => ConnectionFactory.ListDatabasesAsync(candidate, readOnly, cts.Token));
                    var text = databaseBox.Text;
                    foreach (var db in databases) databaseBox.Items.Add(db);
                    databaseBox.Text = text;
                }
            }
            catch (Exception ex) when (!cts.IsCancellationRequested)
            {
                testResult.Text = ex.Message;
                testResult.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            }
            catch (OperationCanceledException) { }
            finally { testButton.IsEnabled = true; }
        };

        var panel = new StackPanel { Spacing = 12, Width = width };
        panel.Children.Add(groupBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(authBox);
        panel.Children.Add(hint);
        panel.Children.Add(serverBox);
        panel.Children.Add(connectionStringBox);
        panel.Children.Add(userBox);
        panel.Children.Add(passwordBox);
        panel.Children.Add(databaseBox);
        panel.Children.Add(options);
        panel.Children.Add(testRow);
        UpdateFields();
        serverBox.Loaded += (_, _) => { if (isNew) serverBox.Focus(FocusState.Programmatic); };

        var dialog = Create(root, isNew ? "New server" : "Edit server", new ScrollViewer { Content = panel, Padding = new Thickness(0, 0, 16, 0) }, isNew ? "Add" : "Save");
        dialog.Resources["ContentDialogMaxWidth"] = 700.0;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var r = Read();
            var missing = r.AuthMode == AuthMode.ConnectionString ? r.ConnectionString.Length == 0 : r.Server.Length == 0;
            if (!missing) return;
            args.Cancel = true;
            testResult.Text = r.AuthMode == AuthMode.ConnectionString ? "Enter a connection string." : "Enter a server name.";
            testResult.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        };
        var result = await dialog.ShowAsync();
        testCts?.Cancel();
        if (result != ContentDialogResult.Primary) return null;
        return (Read(), groups[Math.Max(0, groupBox.SelectedIndex)]);
    }

    // ------------------------------------------------------------------ about & updates

    /// <summary>Shows About. Returns an update if the user found one via "Check for updates".</summary>
    public static async Task<UpdateInfo?> ShowAboutAsync(XamlRoot root)
    {
        var version = UpdateService.CurrentVersion.ToString(3);

        var art = new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AboutArt.png"))),
            Width = 200, Height = 200,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var shortcuts = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (string Keys, string Action)[] keys =
        [
            ("F5 / Ctrl+Enter / Ctrl+E", "Execute selection or whole script"),
            ("Ctrl+N", "New query on the selected server"),
            ("Ctrl+O / Ctrl+S", "Open / save .sql file"),
            ("Ctrl+Space", "Autocomplete tables, columns, keywords"),
            ("Ctrl+F / F3", "Find in script"),
            ("Ctrl+Shift+F", "Search servers and databases"),
            ("Ctrl+R", "Show or hide the results pane"),
            ("Ctrl+W", "Close query window"),
            ("Ctrl+C / Ctrl+Shift+C", "Copy grid rows (with headers)"),
        ];
        for (var i = 0; i < keys.Length; i++)
        {
            shortcuts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = keys[i].Keys, FontFamily = (FontFamily)Application.Current.Resources["CodeFont"] };
            var a = new TextBlock { Text = keys[i].Action, Foreground = Secondary, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, i); Grid.SetRow(a, i); Grid.SetColumn(a, 1);
            shortcuts.Children.Add(k); shortcuts.Children.Add(a);
        }

        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(art);
        panel.Children.Add(new TextBlock
        {
            Text = "Sqlzilla", HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock { Text = $"Version {version}", Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = "A lightweight SQL Server query tool — colour-coded server groups, Entra ID sign-in and fast result grids.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock { Text = "Keyboard shortcuts", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(shortcuts);
        panel.Children.Add(new TextBlock
        {
            Text = "Copyright (c) 2026 James West. Released under the MIT License.",
            Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        panel.Children.Add(new HyperlinkButton
        {
            Content = $"github.com/{UpdateService.Repo}",
            NavigateUri = new Uri($"https://github.com/{UpdateService.Repo}"),
            Padding = new Thickness(0),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = new ScrollViewer { Content = panel },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        UpdateInfo? found = null;
        var checkResult = new TextBlock { Foreground = Secondary, TextWrapping = TextWrapping.Wrap };
        var checkButton = new Button { Content = "Check for updates" };
        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            checkResult.Text = "Checking…";
            try
            {
                found = await UpdateService.CheckAsync(CancellationToken.None);
                if (found is null) checkResult.Text = "You're up to date.";
                else dialog.Hide();
            }
            catch (Exception ex) { checkResult.Text = $"Couldn't check for updates: {ex.Message}"; }
            finally { checkButton.IsEnabled = true; }
        };
        panel.Children.Add(checkButton);
        panel.Children.Add(checkResult);

        await dialog.ShowAsync();
        return found;
    }

    /// <summary>Asks to install an update; on accept downloads it, launches the installer and exits the app.</summary>
    public static async Task OfferUpdateAsync(XamlRoot root, UpdateInfo update)
    {
        await WhenNoDialogOpenAsync(root);
        var message = new TextBlock
        {
            Text = $"Sqlzilla {update.Version} is available (you have {UpdateService.CurrentVersion.ToString(3)}). Update now? Sqlzilla will restart.",
            TextWrapping = TextWrapping.Wrap,
        };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(message);
        panel.Children.Add(progress);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Update available",
            Content = panel,
            PrimaryButtonText = "Update now",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            message.Text = $"Downloading Sqlzilla {update.Version}…";
            try
            {
                var installer = await UpdateService.DownloadAsync(update, CancellationToken.None);
                UpdateService.StartInstaller(installer);
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                progress.Visibility = Visibility.Collapsed;
                message.Text = $"Update failed: {ex.Message}";
            }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
}
