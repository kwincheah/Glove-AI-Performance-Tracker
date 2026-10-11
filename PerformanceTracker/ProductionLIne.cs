using ImageSourceModuleCs;
using IMVSCnnDetectModuCs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VM.Core;
using VMControls.Interface;

namespace PerformanceTracker
{
    
    /// Represents a single production line instance, managing its own vision flow, 
    /// tools, and statistical counters for AI performance tracking.
    
    public class ProductionLine : INotifyPropertyChanged
    {
        #region Fields and Synchronization

        private readonly AutoResetEvent _waitHandle = new AutoResetEvent(false);
        private readonly object _lock = new object();
        private readonly List<string> _stainOkPaths = new List<string>();
        private readonly List<string> _debugLogs = new List<string>();

        // Store the source paths of mispredicted images for later export
        public List<string> _mispredictedPaths = new List<string>();

        #endregion

        #region Core Configuration Properties

        public string LineName { get; set; }
        public string FolderPath { get; set; }
        public string ModelPath { get; set; }
        public string SaveImagePath { get; set; }
        public VmProcedure VmFlow { get; set; }
        public bool IsBlackGlove { get; set; }

        #endregion

        #region UI Bound Properties (Counters & Status)

        private int _totalImages;
        public int TotalImages
        {
            get => _totalImages;
            set { _totalImages = value; OnPropertyChanged(); }
        }

        private int _currentProgress;
        public int CurrentProgress
        {
            get => _currentProgress;
            set { _currentProgress = value; OnPropertyChanged(); }
        }

        private string _mode;
        public string Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPostMode));
            }
        }

        public bool IsPostMode => _mode == "POST";

        private IMVSCnnDetectModuTool _aiModule;
        public IMVSCnnDetectModuTool AiModule
        {
            get => _aiModule;
            set { _aiModule = value; OnPropertyChanged(); }
        }

        private ImageSourceModuleTool _imgTool;
        public ImageSourceModuleTool ImgSourceModule
        {
            get => _imgTool;
            set { _imgTool = value; OnPropertyChanged(); }
        }

        private string _status = "Waiting"; // Default status
        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusPriority)); // Trigger sort refresh in UI
            }
        }

        
        /// Logic for dynamic sorting in the WPF ItemsControl.
        
        public int StatusPriority
        {
            get
            {
                if (Status == "Running") return 2;
                if (Status == "Completed") return 1;
                if (Status == "Waiting") return 3;
                if (Status == "Empty") return 4;
                return 5;
            }
        }

        #endregion

        #region Statistics and Counters

        private int _ok, _okng, _stain, _stainOk, _tear, _tearOk, _fp, _fpOk;

        public int OK => _ok;
        public int OKNG => _okng;
        public double OKPcnt => (_ok + _okng) == 0 ? 0 : (double)_ok / (_ok + _okng);

        public int Tear => _tear;
        public int TearOK => _tearOk;
        public double TearPcnt => (_tear + _tearOk) == 0 ? 0 : (double)_tear / (_tear + _tearOk);

        public int FP => _fp;
        public int FPOK => _fpOk;
        public double FPPcnt => (_fp + _fpOk) == 0 ? 0 : (double)_fpOk / (_fp + _fpOk);

        public int Stain => _stain;
        public int StainOK => _stainOk;
        public double StainPcnt => (_stain + _stainOk) == 0 ? 0 : (double)_stain / (_stain + _stainOk);

        #endregion

        #region Vision Master Integration Methods

        
        /// Callback triggered when the Vision Master procedure finishes a cycle.
        
        private void OnWorkStatusCallback(object sender, EventArgs e)
        {
            if (e is ValueEventArgs args && args.Value != null)
            {
                _waitHandle.Set(); // Signal that data is ready to be read
            }
        }

        
        /// Connects C# properties to the underlying Vision Master tool instances.
        
        public void InitializeTools()
        {
            if (VmFlow == null) return;

            // Initialize AI Module
            string aiPath = $"{VmFlow.FullName}.LS_AI.C0";
            var tool = VmSolution.Instance[aiPath] as IMVSCnnDetectModuTool;

            if (tool != null)
            {
                var param = tool.ModuParams;
                param.LoadModelPath = ModelPath;
                param.MinScore = 0.5;
                tool.ModuParams = param;
                this.AiModule = tool;
            }

            // Initialize Image Source Tool based on Mode
            string imgPath = (Mode == "PRE") ? $"{VmFlow.FullName}.ManualImage" : $"{VmFlow.FullName}.Image Source1";
            _imgTool = VmSolution.Instance[imgPath] as ImageSourceModuleTool;
            this.ImgSourceModule = _imgTool;

            // Subscribe to procedure completion callback
            VmFlow.OnWorkEndStatusCallBack -= OnWorkStatusCallback;
            VmFlow.OnWorkEndStatusCallBack += OnWorkStatusCallback;

            _waitHandle.Reset();
        }

        
        /// Executes the vision flow for a single image.
        
        public void RunNext(string currentPath)
        {
            if (VmFlow == null || _imgTool == null || AiModule == null)
            {
                System.Diagnostics.Debug.WriteLine($"!!! ERROR: Tools not initialized for {LineName}");
                return;
            }

            _waitHandle.Reset();

            try
            {
                _imgTool.ClearAllInputImage();
                _imgTool.AddInputImageByPath(currentPath);

                Thread.Sleep(20); // Stability delay

                _imgTool.Run();
                VmFlow.Run();

                // Wait for the callback signal (max 5 seconds)
                if (!_waitHandle.WaitOne(5000))
                {
                    System.Diagnostics.Debug.WriteLine($"TIMEOUT: {LineName} stalled on {currentPath}");
                }
                else
                {
                    UpdateResult(currentPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RUN ERROR on {LineName}: {ex.Message}");
            }
        }

        
        /// Parses the AI results and updates the statistical counters.
        
        public void UpdateResult(string imgPath)
        {
            try
            {
                // Mandatory Delay: Ensure the AI buffer is fully written to memory
                Thread.Sleep(50);

                if (AiModule?.ModuResult?.ClassName == null) return;

                List<string> results = AiModule.ModuResult.ClassName;
                string folderName = Path.GetFileName(Path.GetDirectoryName(imgPath));
                string fileName = Path.GetFileName(imgPath);

                string[] defects = { "Stain", "Tear", "BadStrip", "Hole", "Beading" };
                bool hasAnyDefect = results.Any(d => defects.Contains(d));
                string resultStr = results.Count > 0 ? string.Join("|", results) : "OK";

                // Thread-safe tallying
                lock (_lock)
                {
                    _debugLogs.Add($"{DateTime.Now:HH:mm:ss.fff},{fileName},{folderName},{resultStr}");

                    if (folderName == "OK")
                    {
                        if (hasAnyDefect) { _okng++; _mispredictedPaths.Add(imgPath); }
                        else _ok++;
                    }
                    else if (folderName == "Stain")
                    {
                        if (results.Contains("Stain")) { _stain++; _mispredictedPaths.Add(imgPath); }
                        else { _stainOk++; _mispredictedPaths.Add(imgPath); }
                    }
                    else if (folderName == "Tear")
                    {
                        bool isTearFolderDefect = results.Any(d => d == "Tear" || d == "Hole" || d == "Beading" || d == "BadStrip");
                        if (isTearFolderDefect) { _tear++; _mispredictedPaths.Add(imgPath); }
                        else _tearOk++;
                    }
                    else if (folderName == "FP")
                    {
                        if (!hasAnyDefect) { _fpOk++; _mispredictedPaths.Add(imgPath); }
                        else _fp++;
                    }
                }

                // Push updates to UI Thread
                Application.Current.Dispatcher.BeginInvoke(new Action(() => NotifyAllCounters()));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Log Error: {ex.Message}");
            }
        }

        #endregion

        #region Export and Logging Methods

        public void SaveDebugLog()
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DebugLogs");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, $"{LineName}_Debug.csv");
                string header = "Timestamp,FileName,ExpectedFolder,DetectedClassName";
                File.WriteAllLines(path, new[] { header }.Concat(_debugLogs));
            }
            catch { }
        }

        public void SaveStainResults()
        {
            if (_stainOkPaths.Count == 0) return;
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string fileName = Path.Combine(dir, $"StainResults_{LineName}_{DateTime.Now:yyyyMMdd_HHmm}.csv");
                string content = "Correct_Stain_Image_Paths" + Environment.NewLine + string.Join(Environment.NewLine, _stainOkPaths);
                File.WriteAllText(fileName, content);
            }
            catch { }
        }

        public void PreparePreview(string firstImagePath)
        {
            if (_imgTool == null) return;
            try
            {
                _imgTool.ClearAllInputImage();
                _imgTool.AddInputImageByPath(firstImagePath);
                _imgTool.Run();
            }
            catch { }
        }

        
        /// Asynchronously copies mispredicted images to a central audit folder.
        
        public void BulkSaveMispredictions()
        {
            // 1. Safety Check
            if (string.IsNullOrEmpty(SaveImagePath) || _mispredictedPaths == null || _mispredictedPaths.Count == 0)
            {
                return;
            }

            // 2. Snapshot the data for thread-safe iteration
            List<string> snapshots;
            lock (_lock)
            {
                snapshots = new List<string>(_mispredictedPaths);
                _mispredictedPaths.Clear(); // Prevent duplicate saves
            }

            Task.Run(() =>
            {
                try
                {
                    foreach (string sourcePath in snapshots)
                    {
                        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) continue;

                        string fileName = Path.GetFileName(sourcePath);
                        string folderName = Path.GetFileName(Path.GetDirectoryName(sourcePath));

                        // Build Path: [SelectedPath]\[LineName]\[OriginalFolderName]
                        string targetDir = Path.Combine(SaveImagePath, LineName, folderName);

                        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                        string destPath = Path.Combine(targetDir, fileName);
                        File.Copy(sourcePath, destPath, true);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Bulk Save Thread Error: {ex.Message}");
                }
            });
        }

        #endregion

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private void NotifyAllCounters()
        {
            OnPropertyChanged(nameof(OK));
            OnPropertyChanged(nameof(OKNG));
            OnPropertyChanged(nameof(OKPcnt));
            OnPropertyChanged(nameof(Tear));
            OnPropertyChanged(nameof(TearOK));
            OnPropertyChanged(nameof(TearPcnt));
            OnPropertyChanged(nameof(Stain));
            OnPropertyChanged(nameof(StainOK));
            OnPropertyChanged(nameof(StainPcnt));
            OnPropertyChanged(nameof(FP));
            OnPropertyChanged(nameof(FPOK));
            OnPropertyChanged(nameof(FPPcnt));
        }

        #endregion
    }
}