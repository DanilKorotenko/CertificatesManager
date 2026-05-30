using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace CertificatesManager;

public partial class MainWindow : Window
{
    private const string CertificatesExtension = "*.cer";
    private const string ProtectedEkuOid = "1.3.6.1.4.1.19398.1.1.8.22";

    private readonly ObservableCollection<CertificateListItem> _certificateFiles = [];
    private readonly string _settingsFilePath;
    private FileSystemWatcher? _folderWatcher;

    public MainWindow()
    {
        InitializeComponent();

        _settingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CertificatesManager",
            "settings.json");

        CertificatesListView.ItemsSource = _certificateFiles;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = LoadSettings();
        ShowFoldersCheckBox.IsChecked = settings?.ShowFolders ?? false;

        if (string.IsNullOrWhiteSpace(settings?.CertificatesFolder))
        {
            SetStatus("Ready");
            return;
        }

        FolderPathTextBox.Text = settings.CertificatesFolder;
        ApplyCertificatesFolder(settings.CertificatesFolder);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            SaveSettings();
        }
        catch (Exception exception)
        {
            SetStatus($"Error saving settings: {exception.Message}");
        }

        DisposeWatcher();
    }

    private void ShowFoldersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (Directory.Exists(FolderPathTextBox.Text))
        {
            ApplyCertificatesFolder(FolderPathTextBox.Text);
        }
        else
        {
            RefreshCurrentFolder();
        }
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folderDialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(FolderPathTextBox.Text)
                ? FolderPathTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (folderDialog.ShowDialog() == true)
        {
            FolderPathTextBox.Text = folderDialog.FolderName;
            ApplyCertificatesFolder(folderDialog.FolderName);
        }
    }

    private void FolderPathTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ApplyCertificatesFolder(FolderPathTextBox.Text);
    }

    private void FolderPathTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyCertificatesFolder(FolderPathTextBox.Text);
    }

    private void CertificatesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CertificatesListView.SelectedItem is not CertificateListItem item || !item.IsNavigable)
        {
            return;
        }

        NavigateToFolder(item.FilePath);
    }

    private void NavigateToFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            SetStatus("Error: certificates folder does not exist.");
            return;
        }

        FolderPathTextBox.Text = folderPath;
        ApplyCertificatesFolder(folderPath);
    }

    private void CertificatesListViewItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListViewItem listViewItem || listViewItem.DataContext is not CertificateListItem item)
        {
            return;
        }

        if (item.Kind != ListItemKind.Certificate)
        {
            listViewItem.ContextMenu = null;
            return;
        }

        var selectedItems = CertificatesListView.SelectedItems
            .Cast<CertificateListItem>()
            .Where(x => x.Kind == ListItemKind.Certificate)
            .ToList();

        var shouldUseBulkMenu = selectedItems.Count > 1 && selectedItems.Contains(item);
        if (shouldUseBulkMenu)
        {
            BuildBulkContextMenu(listViewItem, selectedItems);
            return;
        }

        var targetBaseName = item.IsRsa
            ? $"{item.PersonName}_RSA"
            : item.PersonName;
        var menuitemBaseName = item.IsRsa
            ? $"{item.PersonName}__RSA"
            : item.PersonName;
        var renameHeader = $"Переіменувати в {menuitemBaseName}";
        var renameMenuItem = new MenuItem
        {
            Header = renameHeader
        };
        renameMenuItem.Click += RenameFileMenuItem_Click;

        var deleteMenuItem = new MenuItem
        {
            Header = "Видалити файл"
        };
        deleteMenuItem.Click += DeleteFileMenuItem_Click;

        var contextMenu = new ContextMenu
        {
            DataContext = item
        };
        contextMenu.Items.Add(renameMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(deleteMenuItem);

        listViewItem.ContextMenu = contextMenu;
    }

    private void BuildBulkContextMenu(ListViewItem listViewItem, List<CertificateListItem> selectedItems)
    {
        var renameMenuItem = new MenuItem
        {
            Header = "Переіменувати відповідно ПІБ та ПІБ__RSA",
            DataContext = selectedItems
        };
        renameMenuItem.Click += BulkRenameFilesMenuItem_Click;

        var deleteMenuItem = new MenuItem
        {
            Header = "Видалити всі",
            DataContext = selectedItems
        };
        deleteMenuItem.Click += BulkDeleteFilesMenuItem_Click;

        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(renameMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(deleteMenuItem);

        listViewItem.ContextMenu = contextMenu;
    }

    private void RenameFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: CertificateListItem item })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.PersonName))
        {
            SetStatus("Error: неможливо перейменувати файл без ПІБ.");
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(item.FilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                SetStatus("Error: невірний шлях до файлу.");
                return;
            }

            var normalizedName = SanitizeFileName(item.PersonName.Trim());
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                SetStatus("Error: некоректне ім'я для перейменування.");
                return;
            }

            var targetFileName = item.IsRsa
                ? $"{normalizedName}_RSA.cer"
                : $"{normalizedName}.cer";

            var targetPath = Path.Combine(directory, targetFileName);
            if (!string.Equals(item.FilePath, targetPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(targetPath))
            {
                SetStatus("Error: файл з таким ім'ям вже існує.");
                return;
            }

            File.Move(item.FilePath, targetPath, overwrite: false);
            RefreshCurrentFolder();
            SetStatus($"Файл перейменовано: {targetFileName}");
        }
        catch (Exception exception)
        {
            SetStatus($"Error: {exception.Message}");
        }
    }

    private void RenameAllButton_Click(object sender, RoutedEventArgs e)
    {
        var items = _certificateFiles
            .Where(x => x.Kind == ListItemKind.Certificate)
            .ToList();

        if (items.Count == 0)
        {
            SetStatus("Error: немає сертифікатів для перейменування.");
            return;
        }

        BulkRenameFiles(items);
    }

    private void BulkRenameFilesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: List<CertificateListItem> items } || items.Count == 0)
        {
            return;
        }

        BulkRenameFiles(items);
    }

    private void BulkRenameFiles(IReadOnlyList<CertificateListItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var selectedSourcePaths = items
            .Select(x => x.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedRenames = new List<(string SourcePath, string TargetPath, string TargetFileName)>();

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.PersonName))
            {
                SetStatus("Error: неможливо перейменувати файл без ПІБ.");
                return;
            }

            var directory = Path.GetDirectoryName(item.FilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                SetStatus("Error: невірний шлях до файлу.");
                return;
            }

            var normalizedName = SanitizeFileName(item.PersonName.Trim());
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                SetStatus("Error: некоректне ім'я для перейменування.");
                return;
            }

            var targetFileName = item.IsRsa
                ? $"{normalizedName}.cer"
                : $"{normalizedName}_RSA.cer";
            var targetPath = Path.Combine(directory, targetFileName);

            if (!string.Equals(item.FilePath, targetPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(targetPath)
                && !selectedSourcePaths.Contains(targetPath))
            {
                SetStatus($"Error: файл {targetFileName} вже існує.");
                return;
            }

            plannedRenames.Add((item.FilePath, targetPath, targetFileName));
        }

        var duplicateTargets = plannedRenames
            .GroupBy(x => x.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.First().TargetFileName)
            .ToList();

        if (duplicateTargets.Count > 0)
        {
            SetStatus($"Error: дублікати імен при перейменуванні ({duplicateTargets[0]}).");
            return;
        }

        try
        {
            foreach (var rename in plannedRenames)
            {
                if (string.Equals(rename.SourcePath, rename.TargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Move(rename.SourcePath, rename.TargetPath, overwrite: false);
            }

            RefreshCurrentFolder();
            SetStatus($"Перейменовано файлів: {plannedRenames.Count}");
        }
        catch (Exception exception)
        {
            SetStatus($"Error: {exception.Message}");
        }
    }

    private void DeleteFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: CertificateListItem item })
        {
            return;
        }

        try
        {
            File.Delete(item.FilePath);
            RefreshCurrentFolder();
            SetStatus($"Файл видалено: {item.FileName}");
        }
        catch (Exception exception)
        {
            SetStatus($"Error: {exception.Message}");
        }
    }

    private void BulkDeleteFilesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: List<CertificateListItem> items } || items.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var item in items)
            {
                if (File.Exists(item.FilePath))
                {
                    File.Delete(item.FilePath);
                }
            }

            RefreshCurrentFolder();
            SetStatus($"Видалено файлів: {items.Count}");
        }
        catch (Exception exception)
        {
            SetStatus($"Error: {exception.Message}");
        }
    }

    private void PersonNameTextBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CopyTextFromCellToClipboard(sender, "ПІБ скопійовано в кліпбоард");
    }

    private void TinTextBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CopyTextFromCellToClipboard(sender, "ІПН скопійовано в кліпбоард");
    }

    private void ApplyCertificatesFolder(string? folderPath)
    {
        DisposeWatcher();

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            _certificateFiles.Clear();
            SetStatus("Error: certificates folder does not exist.");
            return;
        }

        try
        {
            RefreshCertificatesList(folderPath);
            StartFolderWatcher(folderPath);
        }
        catch (Exception exception)
        {
            _certificateFiles.Clear();
            SetStatus($"Error: {exception.Message}");
        }
    }

    private void RefreshCertificatesList(string folderPath)
    {
        _certificateFiles.Clear();

        if (ShowFoldersCheckBox.IsChecked == true)
        {
            var parentDirectory = Directory.GetParent(folderPath);
            if (parentDirectory is not null)
            {
                _certificateFiles.Add(new CertificateListItem
                {
                    Kind = ListItemKind.ParentDirectory,
                    FileName = "..",
                    FilePath = parentDirectory.FullName
                });
            }

            foreach (var directoryPath in Directory
                         .GetDirectories(folderPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                _certificateFiles.Add(new CertificateListItem
                {
                    Kind = ListItemKind.Directory,
                    FileName = Path.GetFileName(directoryPath) ?? directoryPath,
                    FilePath = directoryPath
                });
            }
        }

        var files = Directory
            .GetFiles(folderPath, CertificatesExtension, SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        string? firstError = null;
        var certificateCount = 0;
        foreach (var filePath in files)
        {
            _certificateFiles.Add(BuildCertificateItem(filePath, out var fileError));
            certificateCount++;
            if (firstError is null && !string.IsNullOrWhiteSpace(fileError))
            {
                firstError = fileError;
            }
        }

        if (!string.IsNullOrWhiteSpace(firstError))
        {
            SetStatus(firstError);
        }
        else
        {
            SetStatus($"Loaded {certificateCount} certificate(s).");
        }
    }

    private static CertificateListItem BuildCertificateItem(string filePath, out string? fileError)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(filePath);

            fileError = null;
            return new CertificateListItem
            {
                Kind = ListItemKind.Certificate,
                FileName = Path.GetFileName(filePath) ?? filePath,
                FilePath = filePath,
                IsProtected = HasProtectedEku(certificate),
                IsRsa = HasRequiredKeyUsage(certificate),
                KeyUsage = ExtractKeyUsage(certificate),
                PersonName = certificate.GetNameInfo(X509NameType.SimpleName, false),
                Tin = ExtractTin(certificate.Subject)
            };
        }
        catch (Exception exception)
        {
            fileError = $"Error reading {Path.GetFileName(filePath)}: {exception.Message}";
            return new CertificateListItem
            {
                Kind = ListItemKind.Certificate,
                FileName = Path.GetFileName(filePath) ?? filePath,
                FilePath = filePath
            };
        }
    }

    private static bool HasProtectedEku(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509EnhancedKeyUsageExtension ekuExtension)
            {
                continue;
            }

            foreach (var oidObject in ekuExtension.EnhancedKeyUsages)
            {
                if (oidObject is System.Security.Cryptography.Oid oid
                    && string.Equals(oid.Value, ProtectedEkuOid, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    private static bool HasRequiredKeyUsage(X509Certificate2 certificate)
    {
        var requiredFlags = X509KeyUsageFlags.DigitalSignature
            | X509KeyUsageFlags.NonRepudiation
            | X509KeyUsageFlags.KeyEncipherment;

        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509KeyUsageExtension keyUsageExtension)
            {
                continue;
            }

            var keyUsage = keyUsageExtension.KeyUsages;
            return (keyUsage & requiredFlags) == requiredFlags;
        }

        return false;
    }

    private static string ExtractKeyUsage(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509KeyUsageExtension keyUsageExtension)
            {
                return FormatKeyUsage(keyUsageExtension);
            }
        }

        return string.Empty;
    }

    private static string FormatKeyUsage(X509KeyUsageExtension keyUsageExtension)
    {
        var flags = keyUsageExtension.KeyUsages;
        var parts = new List<string>();

        if (flags.HasFlag(X509KeyUsageFlags.DigitalSignature))
        {
            parts.Add("Digital Signature");
        }

        if (flags.HasFlag(X509KeyUsageFlags.NonRepudiation))
        {
            parts.Add("Non-Repudiation");
        }

        if (flags.HasFlag(X509KeyUsageFlags.KeyEncipherment))
        {
            parts.Add("Key Encipherment");
        }

        if (flags.HasFlag(X509KeyUsageFlags.DataEncipherment))
        {
            parts.Add("Data Encipherment");
        }

        if (flags.HasFlag(X509KeyUsageFlags.KeyAgreement))
        {
            parts.Add("Key Agreement");
        }

        if (flags.HasFlag(X509KeyUsageFlags.KeyCertSign))
        {
            parts.Add("Certificate Signing");
        }

        if (flags.HasFlag(X509KeyUsageFlags.CrlSign))
        {
            parts.Add("CRL Signing");
        }

        if (flags.HasFlag(X509KeyUsageFlags.EncipherOnly))
        {
            parts.Add("Encipher Only");
        }

        if (flags.HasFlag(X509KeyUsageFlags.DecipherOnly))
        {
            parts.Add("Decipher Only");
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var hexValue = ((int)flags).ToString("x", CultureInfo.InvariantCulture);
        return $"{string.Join(", ", parts)} ({hexValue})";
    }

    private static string ExtractTin(string subject)
    {
        var match = Regex.Match(subject, @"SERIALNUMBER\s*=\s*TINUA-(\d{10})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private void StartFolderWatcher(string folderPath)
    {
        var showFolders = ShowFoldersCheckBox.IsChecked == true;

        _folderWatcher = showFolders
            ? new FileSystemWatcher(folderPath)
            : new FileSystemWatcher(folderPath, CertificatesExtension);

        _folderWatcher.IncludeSubdirectories = false;
        _folderWatcher.NotifyFilter = showFolders
            ? NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.CreationTime
            : NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime;

        _folderWatcher.Changed += FolderWatcher_OnChanged;
        _folderWatcher.Created += FolderWatcher_OnChanged;
        _folderWatcher.Deleted += FolderWatcher_OnChanged;
        _folderWatcher.Renamed += FolderWatcher_OnChanged;
        _folderWatcher.Error += FolderWatcher_Error;
        _folderWatcher.EnableRaisingEvents = true;
    }

    private void FolderWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (Directory.Exists(FolderPathTextBox.Text))
                {
                    RefreshCertificatesList(FolderPathTextBox.Text);
                }
                else
                {
                    _certificateFiles.Clear();
                    SetStatus("Error: certificates folder is no longer available.");
                }
            }
            catch (Exception exception)
            {
                SetStatus($"Error: {exception.Message}");
            }
        });
    }

    private void FolderWatcher_Error(object sender, ErrorEventArgs e)
    {
        Dispatcher.Invoke(() => SetStatus($"Watcher error: {e.GetException().Message}"));
    }

    private AppSettings? LoadSettings()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch
        {
            SetStatus("Error loading settings file.");
            return null;
        }
    }

    private void SaveSettings()
    {
        var settingsDirectory = Path.GetDirectoryName(_settingsFilePath);
        if (string.IsNullOrWhiteSpace(settingsDirectory))
        {
            return;
        }

        Directory.CreateDirectory(settingsDirectory);

        var settings = new AppSettings
        {
            CertificatesFolder = FolderPathTextBox.Text?.Trim(),
            ShowFolders = ShowFoldersCheckBox.IsChecked == true
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_settingsFilePath, json);
    }

    private void DisposeWatcher()
    {
        if (_folderWatcher is null)
        {
            return;
        }

        _folderWatcher.EnableRaisingEvents = false;
        _folderWatcher.Changed -= FolderWatcher_OnChanged;
        _folderWatcher.Created -= FolderWatcher_OnChanged;
        _folderWatcher.Deleted -= FolderWatcher_OnChanged;
        _folderWatcher.Renamed -= FolderWatcher_OnChanged;
        _folderWatcher.Error -= FolderWatcher_Error;
        _folderWatcher.Dispose();
        _folderWatcher = null;
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private void CopyTextFromCellToClipboard(object sender, string successMessage)
    {
        if (sender is not TextBlock textBlock || string.IsNullOrWhiteSpace(textBlock.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(textBlock.Text);
            SetStatus(successMessage);
        }
        catch (Exception exception)
        {
            SetStatus($"Error: {exception.Message}");
        }
    }

    private void RefreshCurrentFolder()
    {
        if (string.IsNullOrWhiteSpace(FolderPathTextBox.Text) || !Directory.Exists(FolderPathTextBox.Text))
        {
            _certificateFiles.Clear();
            return;
        }

        RefreshCertificatesList(FolderPathTextBox.Text);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        foreach (var invalidCharacter in invalidCharacters)
        {
            fileName = fileName.Replace(invalidCharacter, '_');
        }

        return fileName;
    }

    private sealed class AppSettings
    {
        public string? CertificatesFolder { get; init; }

        public bool ShowFolders { get; init; }
    }

    private enum ListItemKind
    {
        ParentDirectory,
        Directory,
        Certificate
    }

    private sealed class CertificateListItem
    {
        public ListItemKind Kind { get; init; } = ListItemKind.Certificate;

        public bool IsProtected { get; init; }

        public bool IsRsa { get; init; }

        public string KeyUsage { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public string FilePath { get; init; } = string.Empty;

        public string PersonName { get; init; } = string.Empty;

        public string Tin { get; init; } = string.Empty;

        public bool IsNavigable => Kind is ListItemKind.ParentDirectory or ListItemKind.Directory;

        public string IsProtectedMark => IsProtected ? "✓" : string.Empty;

        public string IsRsaMark => IsRsa ? "✓" : string.Empty;
    }
}