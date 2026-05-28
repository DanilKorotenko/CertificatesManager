using System.Collections.ObjectModel;
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
        var savedFolder = LoadSavedCertificatesFolder();
        if (string.IsNullOrWhiteSpace(savedFolder))
        {
            return;
        }

        FolderPathTextBox.Text = savedFolder;
        ApplyCertificatesFolder(savedFolder);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveCertificatesFolder(FolderPathTextBox.Text);
        DisposeWatcher();
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

    private void ApplyCertificatesFolder(string? folderPath)
    {
        DisposeWatcher();

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            _certificateFiles.Clear();
            return;
        }

        RefreshCertificatesList(folderPath);
        StartFolderWatcher(folderPath);
    }

    private void RefreshCertificatesList(string folderPath)
    {
        var files = Directory
            .GetFiles(folderPath, CertificatesExtension, SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _certificateFiles.Clear();
        foreach (var filePath in files)
        {
            _certificateFiles.Add(BuildCertificateItem(filePath));
        }
    }

    private static CertificateListItem BuildCertificateItem(string filePath)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(filePath);

            return new CertificateListItem
            {
                FileName = Path.GetFileName(filePath) ?? filePath,
                IsProtected = HasProtectedEku(certificate),
                IsRsa = HasRequiredKeyUsage(certificate),
                PersonName = certificate.GetNameInfo(X509NameType.SimpleName, false),
                Tin = ExtractTin(certificate.Subject)
            };
        }
        catch
        {
            return new CertificateListItem
            {
                FileName = Path.GetFileName(filePath) ?? filePath
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

    private static string ExtractTin(string subject)
    {
        var match = Regex.Match(subject, @"SERIALNUMBER\s*=\s*TINUA-(\d{10})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private void StartFolderWatcher(string folderPath)
    {
        _folderWatcher = new FileSystemWatcher(folderPath, CertificatesExtension)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };

        _folderWatcher.Changed += FolderWatcher_OnChanged;
        _folderWatcher.Created += FolderWatcher_OnChanged;
        _folderWatcher.Deleted += FolderWatcher_OnChanged;
        _folderWatcher.Renamed += FolderWatcher_OnChanged;
        _folderWatcher.EnableRaisingEvents = true;
    }

    private void FolderWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (Directory.Exists(FolderPathTextBox.Text))
            {
                RefreshCertificatesList(FolderPathTextBox.Text);
            }
            else
            {
                _certificateFiles.Clear();
            }
        });
    }

    private string? LoadSavedCertificatesFolder()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            return settings?.CertificatesFolder;
        }
        catch
        {
            return null;
        }
    }

    private void SaveCertificatesFolder(string? folderPath)
    {
        var settingsDirectory = Path.GetDirectoryName(_settingsFilePath);
        if (string.IsNullOrWhiteSpace(settingsDirectory))
        {
            return;
        }

        Directory.CreateDirectory(settingsDirectory);

        var settings = new AppSettings
        {
            CertificatesFolder = folderPath?.Trim()
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
        _folderWatcher.Dispose();
        _folderWatcher = null;
    }

    private sealed class AppSettings
    {
        public string? CertificatesFolder { get; init; }
    }

    private sealed class CertificateListItem
    {
        public bool IsProtected { get; init; }

        public bool IsRsa { get; init; }

        public string FileName { get; init; } = string.Empty;

        public string PersonName { get; init; } = string.Empty;

        public string Tin { get; init; } = string.Empty;

        public string IsProtectedMark => IsProtected ? "✓" : string.Empty;

        public string IsRsaMark => IsRsa ? "✓" : string.Empty;
    }
}