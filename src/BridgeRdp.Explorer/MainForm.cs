using System.Collections;
using System.Diagnostics;
using BridgeRdp.Protocol;
using BridgeRdp.Files;

namespace BridgeRdp.Explorer;

/// <summary>
/// Локальный проводник по файловой системе целевой RDP-машины.
/// </summary>
internal sealed class MainForm : Form
{
    private static string RootTitle => Program.Legacy ? "Компьютер RDP-клиента" : "Удалённый компьютер";
    private const string NewFolderName = "Новая папка";
    private const string FolderImageKey = "folder";

    private readonly ToolStripButton _backButton = new("← Назад") { ToolTipText = "Назад (Alt+←)" };
    private readonly ToolStripButton _upButton = new("↑ Вверх") { ToolTipText = "Вверх (Backspace)" };
    private readonly ToolStripButton _refreshButton = new("Обновить") { ToolTipText = "Обновить (F5)" };
    private readonly ToolStripButton _downloadButton = new("Скачать…") { ToolTipText = "Скачать выбранное на этот компьютер" };
    private readonly ToolStripButton _uploadButton = new("Загрузить…") { ToolTipText = "Загрузить файлы в открытую папку (или перетащите их в окно)" };
    private readonly ToolStripButton _uploadFolderButton = new("Загрузить папку…");
    private readonly ToolStripButton _newFolderButton = new(NewFolderName);
    private readonly ToolStripButton _renameButton = new("Переименовать") { ToolTipText = "Переименовать (F2)" };
    private readonly ToolStripButton _deleteButton = new("Удалить") { ToolTipText = "Удалить (Del)" };
    private readonly ToolStripButton _reconnectButton = new("Переподключить") { Alignment = ToolStripItemAlignment.Right };

    private readonly ToolStrip _addressStrip = new() { GripStyle = ToolStripGripStyle.Hidden, Stretch = true };
    private readonly ToolStripLabel _addressLabel = new("Путь:");
    private readonly ToolStripTextBox _addressBox = new() { AutoSize = false };
    private readonly ToolStripButton _goButton = new("Перейти");

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        LabelEdit = true,
        AllowDrop = true,
    };

    private readonly ImageList _icons = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = SystemInformation.SmallIconSize };
    private readonly Dictionary<string, string> _typeNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly ToolStripStatusLabel _statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progressBar = new() { Visible = false };
    private readonly ToolStripStatusLabel _cancelLink = new("Отмена") { IsLink = true, Visible = false };

    private readonly Stack<string?> _history = new();
    private BridgeClient? _client;
    private bool _connecting;
    private bool _busy;
    private CancellationTokenSource? _operationCts;
    private string? _currentPath; // null — корень со списком дисков
    private int _sortColumn;
    private bool _sortDescending;

    public MainForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Text = Program.Legacy ? "BridgeRdp Explorer — файлы клиента" : $"BridgeRdp Explorer — удалённые файлы (порт {Program.Port})";

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        toolbar.Items.AddRange(
        [
            _backButton, _upButton, _refreshButton, new ToolStripSeparator(),
            _downloadButton, _uploadButton, _uploadFolderButton, new ToolStripSeparator(),
            _newFolderButton, _renameButton, _deleteButton, _reconnectButton,
        ]);

        _addressStrip.Items.AddRange([_addressLabel, _addressBox, _goButton]);
        _addressStrip.Resize += (_, _) => FitAddressBox();

        _list.Columns.Add("Имя", 340);
        _list.Columns.Add("Дата изменения", 150);
        _list.Columns.Add("Тип", 170);
        _list.Columns.Add("Размер", 180, HorizontalAlignment.Right);
        _list.SmallImageList = _icons;
        _list.ListViewItemSorter = new ItemComparer(_sortColumn, _sortDescending);
        _list.ContextMenuStrip = CreateContextMenu();

        var statusStrip = new StatusStrip();
        statusStrip.Items.AddRange([_statusLabel, _progressBar, _cancelLink]);

        // Порядок важен для докинга: последний добавленный прижимается к краю первым.
        Controls.Add(_list);
        Controls.Add(_addressStrip);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);

        _icons.Images.Add(FolderImageKey, ShellIcons.Stock(StockIconId.Folder));

        _backButton.Click += async (_, _) => await GoBackAsync();
        _upButton.Click += async (_, _) => await GoUpAsync();
        _refreshButton.Click += async (_, _) => await NavigateAsync(_currentPath, addToHistory: false);
        _downloadButton.Click += async (_, _) => await DownloadSelectedAsync();
        _uploadButton.Click += async (_, _) => await UploadWithDialogAsync();
        _uploadFolderButton.Click += async (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Выберите локальную папку для загрузки", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) await UploadAsync([dialog.SelectedPath]);
        };
        _newFolderButton.Click += async (_, _) => await CreateFolderAsync();
        _renameButton.Click += (_, _) => BeginRename();
        _deleteButton.Click += async (_, _) => await DeleteSelectedAsync();
        _reconnectButton.Click += async (_, _) => await ConnectAsync();
        _goButton.Click += async (_, _) => await GoToAddressAsync();
        _cancelLink.Click += (_, _) => _operationCts?.Cancel();
        _addressBox.KeyDown += OnAddressKeyDown;

        _list.ItemActivate += async (_, _) => await OpenSelectedAsync();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.KeyDown += OnListKeyDown;
        _list.ColumnClick += OnColumnClick;
        _list.BeforeLabelEdit += OnBeforeLabelEdit;
        _list.AfterLabelEdit += OnAfterLabelEdit;
        _list.DragEnter += OnDragEnter;
        _list.DragDrop += OnDragDrop;

        UpdateButtons();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitAddressBox();
        await ConnectAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _operationCts?.Cancel();
        _client?.Dispose();
        base.OnFormClosed(e);
    }

    // ---- Подключение -------------------------------------------------------------

    private async Task ConnectAsync()
    {
        if (_connecting)
        {
            return;
        }

        _connecting = true;
        _client?.Dispose();
        _client = null;
        SetStatus("Подключение к файловому агенту…");
        UpdateButtons();

        try
        {
            var client = await Program.ConnectAsync();
            if (IsDisposed) { client.Dispose(); return; }
            client.Disconnected += error =>
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(() => OnDisconnected(client, error));
                }
            };
            _client = client;
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось подключиться: {ex.Message}");
        }
        finally
        {
            _connecting = false;
            UpdateButtons();
        }

        if (_client is not null)
        {
            await NavigateAsync(_currentPath, addToHistory: false);
        }
    }

    private void OnDisconnected(BridgeClient client, Exception? error)
    {
        if (_client != client)
        {
            return;
        }

        _client.Dispose();
        _client = null;
        _list.Items.Clear();
        SetStatus($"Соединение закрыто{(error is null ? "" : $": {error.Message}")}. Нажмите «Переподключить».");
        UpdateButtons();
    }

    // ---- Навигация ---------------------------------------------------------------

    private Task NavigateAsync(string? path, bool addToHistory = true)
    {
        var previous = _currentPath;
        return RunAsync(async (client, ct) =>
        {
            await LoadAsync(client, path, ct);
            if (addToHistory && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
            {
                _history.Push(previous);
            }
        });
    }

    private Task GoBackAsync() =>
        _history.Count > 0 ? NavigateAsync(_history.Pop(), addToHistory: false) : Task.CompletedTask;

    private Task GoUpAsync() =>
        _currentPath is { } path ? NavigateAsync(GetParent(path)) : Task.CompletedTask;

    private Task GoToAddressAsync()
    {
        var text = _addressBox.Text.Trim();
        if (text.Length == 2 && text[1] == ':')
        {
            text += "\\"; // "C:" без слеша означает текущую папку на диске, а не его корень
        }

        return NavigateAsync(text.Length == 0 ? null : text);
    }

    private async Task OpenSelectedAsync()
    {
        if (SelectedItems() is not [var item])
        {
            return;
        }

        if (item.Kind != ItemKind.File)
        {
            await NavigateAsync(item.FullPath);
            return;
        }

        // Файл: скачиваем во временную папку и открываем программой по умолчанию.
        await RunAsync(async (client, ct) =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "BridgeRdp", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            var localPath = Path.Combine(folder, item.Name);
            await TransferFileAsync("Скачивание", item.Name, progress => client.DownloadFileAsync(item.FullPath, localPath, progress, ct));
            SetStatus($"Открыт: {item.Name}");
            Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
        }, cancellable: true);
    }

    /// <summary>Загружает содержимое папки (или список дисков) в ListView.</summary>
    private async Task LoadAsync(BridgeClient client, string? path, CancellationToken ct, string? select = null)
    {
        SetStatus("Загрузка…");

        _list.BeginUpdate();
        try
        {
            if (path is null)
            {
                var drives = await client.ListDrivesAsync(ct);
                _list.Items.Clear();
                foreach (var drive in drives)
                {
                    _list.Items.Add(CreateDriveItem(drive));
                }
            }
            else
            {
                var entries = await client.ListDirectoryAsync(path, ct);
                _list.Items.Clear();
                foreach (var entry in entries)
                {
                    _list.Items.Add(CreateEntryItem(path, entry));
                }
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _currentPath = path;
        _addressBox.Text = path ?? "";
        Text = $"BridgeRdp Explorer — {path ?? RootTitle}";
        SetStatus($"Элементов: {_list.Items.Count}");

        if (select is not null)
        {
            var item = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((ItemInfo)i.Tag!).Name.Equals(select, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                _list.SelectedItems.Clear();
                item.Selected = item.Focused = true;
                item.EnsureVisible();
            }
        }
    }

    // ---- Операции ----------------------------------------------------------------

    private async Task DownloadSelectedAsync()
    {
        var items = SelectedItems().Where(i => i.Kind != ItemKind.Drive).ToList();
        if (items.Count == 0)
        {
            return;
        }

        using var dialog = new FolderBrowserDialog { Description = "Куда сохранить файлы", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await RunAsync(async (client, ct) =>
        {
            foreach (var item in items)
            {
                await DownloadItemAsync(client, item.FullPath, item.Kind == ItemKind.Directory, Path.Combine(dialog.SelectedPath, item.Name), ct);
            }

            SetStatus($"Скачано в {dialog.SelectedPath}");
        }, cancellable: true);
    }

    private async Task DownloadItemAsync(BridgeClient client, string remotePath, bool isDirectory, string localPath, CancellationToken ct)
    {
        if (!isDirectory)
        {
            await TransferFileAsync("Скачивание", Path.GetFileName(remotePath), progress => client.DownloadFileAsync(remotePath, localPath, progress, ct));
            return;
        }

        Directory.CreateDirectory(localPath);
        foreach (var entry in await client.ListDirectoryAsync(remotePath, ct))
        {
            // Junction-точки ("Application Data" и т.п.) пропускаем — они ведут в циклы или закрыты.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.IsDirectory)
            {
                continue;
            }

            await DownloadItemAsync(client, Path.Combine(remotePath, entry.Name), entry.IsDirectory, Path.Combine(localPath, entry.Name), ct);
        }
    }

    private async Task UploadWithDialogAsync()
    {
        using var dialog = new OpenFileDialog { Title = "Выберите локальные файлы для загрузки", Multiselect = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await UploadAsync(dialog.FileNames);
        }
    }

    private Task UploadAsync(IReadOnlyList<string> localPaths)
    {
        if (_currentPath is not { } target)
        {
            MessageBox.Show(this, "Сначала откройте папку, в которую нужно загрузить файлы.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return Task.CompletedTask;
        }

        return RunAsync(async (client, ct) =>
        {
            try
            {
                foreach (var localPath in localPaths)
                {
                    var name = Path.GetFileName(localPath.TrimEnd('\\'));
                    if (name.Length > 0)
                    {
                        await UploadItemAsync(client, localPath, Path.Combine(target, name), ct);
                    }
                }
            }
            finally
            {
                await LoadAsync(client, target, CancellationToken.None);
            }
        }, cancellable: true);
    }

    private async Task UploadItemAsync(BridgeClient client, string localPath, string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.GetAttributes(localPath).HasFlag(FileAttributes.ReparsePoint) && Directory.Exists(localPath)) return;
        if (!Directory.Exists(localPath))
        {
            await TransferFileAsync("Загрузка", Path.GetFileName(localPath), progress => client.UploadFileAsync(localPath, remotePath, progress, ct));
            return;
        }

        await client.CreateDirectoryAsync(remotePath, ct);
        foreach (var child in Directory.EnumerateFileSystemEntries(localPath))
        {
            await UploadItemAsync(client, child, Path.Combine(remotePath, Path.GetFileName(child)), ct);
        }
    }

    private async Task CreateFolderAsync()
    {
        if (_currentPath is not { } folder)
        {
            return;
        }

        var existing = _list.Items.Cast<ListViewItem>().Select(i => ((ItemInfo)i.Tag!).Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = NewFolderName;
        for (var i = 2; existing.Contains(name); i++)
        {
            name = $"{NewFolderName} ({i})";
        }

        await RunAsync(async (client, ct) =>
        {
            await client.CreateDirectoryAsync(Path.Combine(folder, name), ct);
            await LoadAsync(client, folder, ct, select: name);
        });

        // Как в Проводнике: сразу предлагаем переименовать.
        BeginRename();
    }

    private void BeginRename()
    {
        if (!_busy && _list.SelectedItems is [var item] && ((ItemInfo)item.Tag!).Kind != ItemKind.Drive)
        {
            item.BeginEdit();
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var items = SelectedItems().Where(i => i.Kind != ItemKind.Drive).ToList();
        if (items.Count == 0 || _currentPath is not { } folder)
        {
            return;
        }

        var question = items.Count == 1 ? $"Удалить «{items[0].Name}»?" : $"Удалить выбранные элементы ({items.Count})?";
        if (MessageBox.Show(this, $"{question}\n\nПапки удаляются вместе с содержимым, корзины нет.", "Удаление",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        await RunAsync(async (client, ct) =>
        {
            try
            {
                foreach (var item in items)
                {
                    SetStatus($"Удаление: {item.Name}");
                    await client.DeleteAsync(item.FullPath, ct);
                }
            }
            finally
            {
                await LoadAsync(client, folder, CancellationToken.None);
            }
        }, cancellable: true);
    }

    /// <summary>
    /// Выполняет операцию с клиентом: блокирует остальные действия, показывает ошибку, поддерживает отмену.
    /// </summary>
    private async Task RunAsync(Func<BridgeClient, CancellationToken, Task> action, bool cancellable = false)
    {
        if (_busy || _client is not { } client)
        {
            return;
        }

        _busy = true;
        _operationCts = new CancellationTokenSource();
        _cancelLink.Visible = cancellable;
        UpdateButtons();

        try
        {
            await action(client, _operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Операция отменена");
        }
        catch (Exception ex) when (_client == client)
        {
            SetStatus($"Ошибка: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Клиент отключился посреди операции — статус уже показал OnDisconnected.
        }
        finally
        {
            _busy = false;
            _operationCts.Dispose();
            _operationCts = null;
            _cancelLink.Visible = false;
            _progressBar.Visible = false;
            UpdateButtons();
        }
    }

    private async Task TransferFileAsync(string verb, string name, Func<IProgress<(long Done, long Total)>, Task> transfer)
    {
        SetStatus($"{verb}: {name}");
        _progressBar.Value = 0;
        _progressBar.Visible = true;

        var progress = new Progress<(long Done, long Total)>(p =>
        {
            _progressBar.Value = p.Total > 0 ? (int)Math.Min(100, p.Done * 100 / p.Total) : 100;
            SetStatus($"{verb}: {name} — {FormatSize(p.Done)} из {FormatSize(p.Total)}");
        });

        await transfer(progress);
    }

    // ---- События UI --------------------------------------------------------------

    private async void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Back:
                e.SuppressKeyPress = true;
                await GoUpAsync();
                break;
            case Keys.Left when e.Alt:
                e.SuppressKeyPress = true;
                await GoBackAsync();
                break;
            case Keys.F5:
                await NavigateAsync(_currentPath, addToHistory: false);
                break;
            case Keys.F2:
                BeginRename();
                break;
            case Keys.Delete:
                await DeleteSelectedAsync();
                break;
            case Keys.A when e.Control:
                foreach (ListViewItem item in _list.Items)
                {
                    item.Selected = true;
                }

                break;
        }
    }

    private async void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            await GoToAddressAsync();
        }
    }

    private void OnBeforeLabelEdit(object? sender, LabelEditEventArgs e)
    {
        e.CancelEdit = _busy || _client is null || ((ItemInfo)_list.Items[e.Item].Tag!).Kind == ItemKind.Drive;
    }

    private async void OnAfterLabelEdit(object? sender, LabelEditEventArgs e)
    {
        // Подпись обновится после перечитывания папки, если клиент подтвердит переименование.
        e.CancelEdit = true;

        var item = (ItemInfo)_list.Items[e.Item].Tag!;
        var newName = e.Label?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name || _currentPath is not { } folder)
        {
            return;
        }

        await RunAsync(async (client, ct) =>
        {
            await client.RenameAsync(item.FullPath, Path.Combine(folder, newName), ct);
            await LoadAsync(client, folder, ct, select: newName);
        });
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        _sortDescending = e.Column == _sortColumn && !_sortDescending;
        _sortColumn = e.Column;
        _list.ListViewItemSorter = new ItemComparer(_sortColumn, _sortDescending);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var canDrop = !_busy && _client is not null && _currentPath is not null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        e.Effect = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await UploadAsync(paths);
        }
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Открыть", null, async (_, _) => await OpenSelectedAsync());
        open.Font = new Font(open.Font, FontStyle.Bold);

        // Пункты меню повторяют кнопки панели и берут у них доступность.
        (ToolStripMenuItem Item, ToolStripButton? Source)[] items =
        [
            (open, null),
            (new ToolStripMenuItem("Скачать…", null, (_, _) => _downloadButton.PerformClick()), _downloadButton),
            (new ToolStripMenuItem("Переименовать", null, (_, _) => BeginRename(), Keys.F2), _renameButton),
            (new ToolStripMenuItem("Удалить", null, (_, _) => _deleteButton.PerformClick(), Keys.Delete), _deleteButton),
            (new ToolStripMenuItem(NewFolderName, null, (_, _) => _newFolderButton.PerformClick()), _newFolderButton),
            (new ToolStripMenuItem("Загрузить сюда…", null, (_, _) => _uploadButton.PerformClick()), _uploadButton),
            (new ToolStripMenuItem("Обновить", null, (_, _) => _refreshButton.PerformClick(), Keys.F5), _refreshButton),
        ];

        menu.Items.AddRange([items[0].Item, items[1].Item, new ToolStripSeparator(), items[2].Item, items[3].Item,
            new ToolStripSeparator(), items[4].Item, items[5].Item, items[6].Item]);

        menu.Opening += (_, _) =>
        {
            foreach (var (item, source) in items)
            {
                item.Enabled = source?.Enabled ?? true;
            }

            open.Enabled = !_busy && _list.SelectedItems.Count == 1;
        };

        return menu;
    }

    // ---- Вспомогательное ---------------------------------------------------------

    private void UpdateButtons()
    {
        var connected = _client is not null;
        var idle = connected && !_busy;
        var selected = SelectedItems();
        var hasFileItems = selected.Any(i => i.Kind != ItemKind.Drive);
        var inFolder = _currentPath is not null;

        _backButton.Enabled = idle && _history.Count > 0;
        _upButton.Enabled = idle && inFolder;
        _refreshButton.Enabled = idle;
        _downloadButton.Enabled = idle && hasFileItems;
        _uploadButton.Enabled = idle && inFolder;
        _uploadFolderButton.Enabled = idle && inFolder;
        _newFolderButton.Enabled = idle && inFolder;
        _renameButton.Enabled = idle && selected.Count == 1 && hasFileItems;
        _deleteButton.Enabled = idle && hasFileItems;
        _reconnectButton.Visible = !connected;
        _reconnectButton.Enabled = !_connecting;
        _addressBox.Enabled = _goButton.Enabled = idle;
    }

    private List<ItemInfo> SelectedItems() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => (ItemInfo)i.Tag!).ToList();

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void FitAddressBox()
    {
        var available = _addressStrip.DisplayRectangle.Width - _addressLabel.Width - _goButton.Width - LogicalToDeviceUnits(16);
        _addressBox.Width = Math.Max(LogicalToDeviceUnits(100), available);
    }

    private ListViewItem CreateDriveItem(DriveEntry drive)
    {
        var typeName = drive.DriveType switch
        {
            nameof(DriveType.Fixed) => "Локальный диск",
            nameof(DriveType.Removable) => "Съёмный диск",
            nameof(DriveType.CDRom) => "CD-дисковод",
            nameof(DriveType.Network) => "Сетевой диск",
            nameof(DriveType.Ram) => "RAM-диск",
            _ => "Диск",
        };
        var title = string.IsNullOrEmpty(drive.Label) ? typeName : drive.Label;
        var size = drive.IsReady ? $"{FormatSize(drive.FreeSpace)} своб. из {FormatSize(drive.TotalSize)}" : "";

        var item = new ListViewItem([$"{title} ({drive.Name.TrimEnd('\\')})", "", typeName, size])
        {
            ImageKey = GetDriveImageKey(drive.DriveType),
            Tag = new ItemInfo(drive.Name, drive.Name, ItemKind.Drive, drive.TotalSize, null),
        };
        return item;
    }

    private ListViewItem CreateEntryItem(string folder, FileEntry entry)
    {
        var extension = entry.IsDirectory ? "" : Path.GetExtension(entry.Name);
        var item = new ListViewItem(
        [
            entry.Name,
            entry.LastWriteTime.ToString("g"),
            entry.IsDirectory ? "Папка с файлами" : GetTypeName(extension),
            entry.IsDirectory ? "" : FormatSize(entry.Size),
        ])
        {
            ImageKey = entry.IsDirectory ? FolderImageKey : GetFileImageKey(extension),
            Tag = new ItemInfo(entry.Name, Path.Combine(folder, entry.Name),
                entry.IsDirectory ? ItemKind.Directory : ItemKind.File, entry.Size, entry.LastWriteTime),
        };

        if (entry.Attributes.HasFlag(FileAttributes.Hidden))
        {
            item.ForeColor = SystemColors.GrayText;
        }

        return item;
    }

    private string GetDriveImageKey(string? driveType)
    {
        var key = "drive:" + driveType;
        if (!_icons.Images.ContainsKey(key))
        {
            var id = driveType switch
            {
                nameof(DriveType.Removable) => StockIconId.DriveRemovable,
                nameof(DriveType.CDRom) => StockIconId.DriveCD,
                nameof(DriveType.Network) => StockIconId.DriveNet,
                nameof(DriveType.Ram) => StockIconId.DriveRam,
                _ => StockIconId.DriveFixed,
            };
            _icons.Images.Add(key, ShellIcons.Stock(id));
        }

        return key;
    }

    private string GetFileImageKey(string extension)
    {
        var key = "ext:" + extension.ToLowerInvariant();
        if (!_icons.Images.ContainsKey(key))
        {
            var (icon, typeName) = ShellIcons.ForExtension(extension);
            _icons.Images.Add(key, icon ?? ShellIcons.Stock(StockIconId.DocumentNoAssociation));
            _typeNames[extension] = typeName;
        }

        return key;
    }

    private string GetTypeName(string extension)
    {
        GetFileImageKey(extension);
        return _typeNames.TryGetValue(extension, out var name) && name.Length > 0 ? name : $"Файл \"{extension.TrimStart('.').ToUpperInvariant()}\"";
    }

    private static string? GetParent(string path) =>
        string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.GetDirectoryName(path.TrimEnd('\\'));

    private static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.#} {units[unit]}";
    }

    private enum ItemKind
    {
        Drive,
        Directory,
        File,
    }

    private sealed record ItemInfo(string Name, string FullPath, ItemKind Kind, long Size, DateTime? Modified);

    /// <summary>Сортировка как в Проводнике: папки всегда выше файлов.</summary>
    private sealed class ItemComparer(int column, bool descending) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var left = (ListViewItem)x!;
            var right = (ListViewItem)y!;
            var a = (ItemInfo)left.Tag!;
            var b = (ItemInfo)right.Tag!;

            var byKind = (a.Kind == ItemKind.File).CompareTo(b.Kind == ItemKind.File);
            if (byKind != 0)
            {
                return byKind;
            }

            var result = column switch
            {
                1 => Nullable.Compare(a.Modified, b.Modified),
                2 => string.Compare(left.SubItems[2].Text, right.SubItems[2].Text, StringComparison.CurrentCultureIgnoreCase),
                3 => a.Size.CompareTo(b.Size),
                _ => 0,
            };

            if (result == 0)
            {
                result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            }

            return descending ? -result : result;
        }
    }
}
