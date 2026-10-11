using ImageSourceModuleCs;
using IMVSCnnDetectModuCs;
using IMVSColorMeasureModuCs;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using VM.Core;
using Microsoft.Win32;
using VM.PlatformSDKCS;
using VMControls.WPF.Release;
using static System.Net.WebRequestMethods;
using System.Runtime.CompilerServices;

namespace PerformanceTracker
{
    public partial class MainWindow : Window
    {
        private readonly List<ProductionLine> _lines = new List<ProductionLine>();
        private VmProcedure _colorCheckProc;

        private bool _isProcessing = false;
        private bool _isPaused = false;
        private ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true); // "True" means the gate is open (running)
        private CancellationTokenSource _cts;
        private string SaveImagePath = "";

        // Using ConcurrentQueue for thread-safety across the 11 flows
        private readonly ConcurrentQueue<VmProcedure> _availableGeneralFlows = new ConcurrentQueue<VmProcedure>();
        private readonly ConcurrentQueue<VmProcedure> _availableBlackFlows = new ConcurrentQueue<VmProcedure>();

        public MainWindow()
        {
            InitializeComponent();
        }

        private float DetectBlackGloves(string rootPath)
        {
            string firstImage = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));

            if (firstImage == null || _colorCheckProc == null) return 0;

            lock (_colorCheckProc)
            {
                try
                {
                    ImageSourceModuleTool imgSource = (ImageSourceModuleTool)VmSolution.Instance[$"{_colorCheckProc.FullName}.Image Source1"];
                    IMVSColorMeasureModuTool measureResult = (IMVSColorMeasureModuTool)VmSolution.Instance[$"{_colorCheckProc.FullName}.Color Measurement1"];

                    if (imgSource != null && measureResult != null)
                    {
                        // 1. Clear input AND outputs to flush SDK memory
                        imgSource.ClearAllInputImage();

                        // 2. Load the image
                        imgSource.AddInputImageByPath(firstImage);

                        // 3. Execution with a tiny handshake delay
                        _colorCheckProc.Run();

                        // 4. Access Violation Prevention: 
                        // Ensure ModuResult exists before accessing C3Mean
                        if (measureResult.ModuResult != null)
                        {
                            return measureResult.ModuResult.C3Mean;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Color Check Access Violation Prevented: {ex.Message}");
                }
            }
            return 0;
        }

        private void LoadInitialData(List<string> selectedPath, string mode, string modelPath, bool HasBlack, string blackModelPath)
        {
            string postExamSol = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources/Solutions", "ExamPost.solw");
            string preExamSol = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources/Solutions", "ExamPre.solw");

            if (mode == "POST") VmSolution.Load(postExamSol);
            else VmSolution.Load(preExamSol);

            List<VmProcedure> procList = new List<VmProcedure>();
            VmSolution.Instance.GetAllProcedureObjects(ref procList);

            List<VmProcedure> black_proc = new List<VmProcedure>();
            for (int i = procList.Count - 1; i >= 0; i--)
            {
                var proc = procList[i];
                if (proc == null) continue;
                if (proc.FullName.StartsWith("B")) { black_proc.Add(proc); procList.RemoveAt(i); }
                else if (proc.FullName.StartsWith("C")) { _colorCheckProc = proc; procList.RemoveAt(i); }
            }
            black_proc.Reverse();

            // Clear and Refill Pools
            while (_availableGeneralFlows.TryDequeue(out _)) ;
            while (_availableBlackFlows.TryDequeue(out _)) ;
            foreach (var p in procList) _availableGeneralFlows.Enqueue(p);
            foreach (var p in black_proc) _availableBlackFlows.Enqueue(p);

            _lines.Clear();
            List<string> finalPaths = new List<string>();


            foreach (string rawPath in selectedPath)
            {
                string normalizedPath = rawPath.TrimEnd(Path.DirectorySeparatorChar);
                string folderName = Path.GetFileName(normalizedPath);

                // If a specific Lxx folder was selected, add it directly
                if (Regex.IsMatch(folderName, @"^L\d+$"))
                {
                    if (!finalPaths.Contains(normalizedPath)) finalPaths.Add(normalizedPath);
                }
                else
                {
                    // If a parent folder was selected, scan for Lxx subdirectories
                    try
                    {
                        string[] subDirs = Directory.GetDirectories(normalizedPath);
                        var lxxFolders = subDirs.Where(d => Regex.IsMatch(Path.GetFileName(d), @"^L\d+$")).ToList();

                        if (lxxFolders.Any())
                        {
                            foreach (var f in lxxFolders)
                                if (!finalPaths.Contains(f)) finalPaths.Add(f);
                        }
                        else
                        {
                            // Fallback: treat the folder itself as the data source
                            if (!finalPaths.Contains(normalizedPath)) finalPaths.Add(normalizedPath);
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error scanning {normalizedPath}: {ex.Message}"); }
                }
            }



            foreach (string dir in finalPaths)
            {
                float channel3 = DetectBlackGloves(dir);
                bool IsBlack = (channel3 > 120);
                _lines.Add(new ProductionLine
                {
                    LineName = Path.GetFileName(dir),
                    FolderPath = dir,
                    Mode = mode,
                    Status = "Waiting",
                    ModelPath = (IsBlack && HasBlack) ? blackModelPath : modelPath,
                    IsBlackGlove = IsBlack,

                });

            }
            LinesList.ItemsSource = null;
            LinesList.ItemsSource = _lines;

            LinesList.ItemsSource = _lines;
            ICollectionView view = CollectionViewSource.GetDefaultView(_lines);

            if (view is ICollectionViewLiveShaping liveView)
            {
                // Tell the view to monitor the "Status" property for sorting changes
                liveView.LiveSortingProperties.Add("Status");
                liveView.IsLiveSorting = true;
            }

            view.SortDescriptions.Clear();
            // This will keep "Running" at the top and "Waiting" at the bottom
            view.SortDescriptions.Add(new SortDescription("Status", ListSortDirection.Ascending));
        }

        private async Task RunAllExamSetsAsync(CancellationToken token)
        {
            GlobalProgressBar.Value = 0;
            SemaphoreSlim semaphore = new SemaphoreSlim(11);
            List<Task> tasks = new List<Task>();

            // Background Progress Updater
            var progressUpdater = Task.Run(async () =>
            {
                try
                {
                    // Safeguard against empty list during Reset
                    while (_lines != null && _lines.Count > 0 && _lines.Any(l => l.Status == "Running" || l.Status == "Waiting" || l.Status == "Paused"))
                    {
                        if (token.IsCancellationRequested) break;
                        double total = _lines.Sum(l => (double)l.TotalImages);
                        double current = _lines.Sum(l => (double)l.CurrentProgress);
                        if (total > 0) Dispatcher.Invoke(() => GlobalProgressBar.Value = (current / total) * 100);
                        await Task.Delay(500);
                    }
                }
                catch { /* Exit silently on Reset */ }

                if (!token.IsCancellationRequested) Dispatcher.Invoke(() => GlobalProgressBar.Value = 100);
            }, token);

            foreach (var line in _lines)
            {
                line.SaveImagePath = SaveImagePath;
                var currentLine = line;
                tasks.Add(Task.Run(async () =>
                {
                    if (token.IsCancellationRequested) return;

                    VmProcedure assignedFlow = null;
                    bool flowIsBlack = currentLine.IsBlackGlove;

                    try
                    {
                        // CRITICAL FIX: Wrap the WaitAsync to handle the Reset signal gracefully
                        await semaphore.WaitAsync(token);

                        // Double check cancellation after waking up from wait
                        if (token.IsCancellationRequested) return;

                        try
                        {
                            // 1. Determine glove color ONCE
                            flowIsBlack = currentLine.IsBlackGlove;

                            // 2. Dequeue from correct pool
                            bool success = flowIsBlack ? _availableBlackFlows.TryDequeue(out assignedFlow)
                                                       : _availableGeneralFlows.TryDequeue(out assignedFlow);

                            // Fallback: If one pool is empty but folders remain, try the other pool
                            if (!success)
                            {
                                success = flowIsBlack ? _availableGeneralFlows.TryDequeue(out assignedFlow)
                                                      : _availableBlackFlows.TryDequeue(out assignedFlow);
                                if (success) flowIsBlack = !flowIsBlack;
                            }

                            if (success && assignedFlow != null)
                            {
                                currentLine.VmFlow = assignedFlow;
                                currentLine.InitializeTools();

                                var imgTool = VmSolution.Instance[$"{assignedFlow.FullName}.Image Source1"] as ImageSourceModuleTool;
                                imgTool?.ClearAllInputImage();
                                Thread.Sleep(50);

                                var files = Directory.GetFiles(currentLine.FolderPath, "*.bmp", SearchOption.AllDirectories).ToList();

                                if (files.Count == 0)
                                {
                                    currentLine.Status = "Empty Folder";
                                    currentLine.AiModule = null;
                                    return;
                                }

                                currentLine.TotalImages = files.Count;
                                currentLine.Status = "Running";

                                for (int i = 0; i < files.Count; i++)
                                {
                                    if (token.IsCancellationRequested) break;

                                    // Check for Pause gate
                                    _pauseEvent.Wait(token);

                                    currentLine.RunNext(files[i]);
                                    currentLine.CurrentProgress = i + 1;
                                }

                                currentLine.Status = token.IsCancellationRequested ? "Stopped" : "Completed";

                                currentLine.SaveDebugLog();
                            }
                            else
                            {
                                currentLine.Status = "No Flow Available";
                            }
                        }
                        catch (Exception ex)
                        {
                            currentLine.Status = "Error: " + ex.Message;
                        }
                        finally
                        {
                            // Release and return flow
                            if (assignedFlow != null)
                            {
                                if (flowIsBlack) _availableBlackFlows.Enqueue(assignedFlow);
                                else _availableGeneralFlows.Enqueue(assignedFlow);
                            }
                            semaphore.Release();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // This block catches the Reset/Stop signal while waiting for the semaphore.
                        // We exit silently as the user requested a stop.
                    }
                }, token));
            }

            // Ensure the progress updater is part of the final await
            tasks.Add(progressUpdater);
            await Task.WhenAll(tasks);
        }

        private async void StartAll_Click(object sender, RoutedEventArgs e)
        {
            // --- 1. HANDLE PAUSE/RESUME LOGIC ---
            if (_isProcessing)
            {
                if (!_isPaused)
                {
                    // SWITCH TO PAUSED
                    _isPaused = true;
                    _pauseEvent.Reset(); // Close the gate - threads will stop at the next image
                    StartBtnText.Text = "Resume Inference";
                    foreach (var l in _lines) if (l.Status == "Running") l.Status = "Paused";
                    // Change color to Yellow/Orange (Optional)
                }
                else
                {
                    // SWITCH TO RUNNING
                    _isPaused = false;
                    _pauseEvent.Set(); // Open the gate - threads continue
                    StartBtnText.Text = "Pause Inference";
                    foreach (var l in _lines) if (l.Status == "Paused") l.Status = "Running";
                    // Change color back to Red or Blue (Optional)
                }
                return;
            }

            // --- 2. HANDLE INITIAL START LOGIC ---
            if (_lines.Count == 0) return;

            _isProcessing = true;
            _isPaused = false;
            _pauseEvent.Set(); // Ensure gate is open
            StartBtnText.Text = "Pause Inference";
            _cts = new CancellationTokenSource();

            try
            {
                await RunAllExamSetsAsync(_cts.Token);
            }
            finally
            {
                _isProcessing = false;
                StartBtnText.Text = "Start Inference";
            }
        }

        // Helper class for the Summary Table format
        public class SummaryRow
        {
            public string LineName { get; set; } // Only first row of the group
            public string Label { get; set; }    // OK, Tear, Stain, FP
            public string Percentage { get; set; } // Specific % for this label
            public string Reading { get; set; }    // Actual count

            public string Status { get; set; } // Status
        }

        private void Summary_Click(object sender, RoutedEventArgs e)
        {
            if (_lines == null || _lines.Count == 0) return;

            List<SummaryRow> summaryData = new List<SummaryRow>();

            foreach (var line in _lines)
            {
                // row 1: OK (Merged Line Name starts here)
                summaryData.Add(new SummaryRow
                {
                    LineName = line.LineName,
                    Label = "OK",
                    Percentage = line.OKPcnt.ToString("P2"),
                    Reading = line.OK.ToString(),
                    Status = line.Status
                });

                // row 2: Tear (Empty LineName creates merged look)
                summaryData.Add(new SummaryRow
                {
                    LineName = line.LineName,
                    Label = "Tear",
                    Percentage = line.TearPcnt.ToString("P2"),
                    Reading = line.Tear.ToString(),
                    Status = line.Status
                });

                // row 3: Stain
                summaryData.Add(new SummaryRow
                {
                    LineName = line.LineName,
                    Label = "Stain",
                    Percentage = line.StainPcnt.ToString("P2"),
                    Reading = line.Stain.ToString(),
                    Status = line.Status
                });

                // row 4: FP
                summaryData.Add(new SummaryRow
                {
                    LineName = line.LineName,
                    Label = "FP",
                    Percentage = line.FPPcnt.ToString("P2"),
                    Reading = line.FP.ToString(),
                    Status = line.Status
                });



                // Add a blank separator row for visual spacing between Production Lines
                summaryData.Add(new SummaryRow { LineName = "", Label = "", Percentage = "", Reading = "", Status = "" });
            }

            SummaryItems.ItemsSource = summaryData;
            SummaryOverlay.Visibility = Visibility.Visible;
        }

        private void CloseSummary_Click(object sender, RoutedEventArgs e)
        {
            SummaryOverlay.Visibility = Visibility.Collapsed;
        }

        private void SaveMisprediction_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CommonOpenFileDialog();
            dialog.IsFolderPicker = true;
            dialog.Title = "Select Save Location";

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                // Path.Combine is safer than manual string adding

                // Generates a string like "Misdetected Image_20260123_1730"
                string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
                string folderName = $"Misdetected Image_{timeStamp}";

                string finalPath = Path.Combine(dialog.FileName, folderName);

                foreach (var line in _lines)
                {
                    line.SaveImagePath = finalPath; // Set the path FIRST
                    line.BulkSaveMispredictions(); // Then run the logic
                }

                MessageBox.Show("Export started in the background. Export to Misdetected Image", "Exporting", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void VmRenderControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is VmRenderControl viewer)
            {
                viewer.SetRenderInterval(50);
                viewer.CoordinateInfoVisible = true;
            }
        }


        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            // 1. Ask for confirmation to prevent accidental data loss
            var result = MessageBox.Show("Are you sure you want to reset? All current results will be cleared.",
                                         "Reset System", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                // 2. Stop ongoing processing immediately
                if (_isProcessing)
                {
                    _cts?.Cancel();
                    _pauseEvent.Set(); // Open the "gate" so threads can hit the cancellation check and exit
                }

                // 3. Clear the DataY
                foreach (var line in _lines)
                {
                    //line._mispredictedPaths.Clear();
                }
                _lines.Clear();
                LinesList.ItemsSource = null; // Unbind UI

                // 4. Reset UI States
                _isProcessing = false;
                _isPaused = false;
                GlobalProgressBar.Value = 0;
                StartBtnText.Text = "Start Inference";

                // 5. Show Welcome Overlay
                WelcomeOverlay.Visibility = Visibility.Visible;

                System.Diagnostics.Debug.WriteLine("System Reset: All data cleared and returned to Welcome screen.");
            }
        }


        private void Load_Click(object sender, RoutedEventArgs e)
        {
            var configWindow = new LoadConfigWindow { Owner = this };

            if (configWindow.ShowDialog() == true)
            {
                // 1. Immediate UI update
                WelcomeOverlay.Visibility = Visibility.Collapsed;

                // 2. Schedule the heavy work to happen AFTER the UI has drawn the spinner
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // This runs the heavy loading logic
                    LoadInitialData(configWindow.SelectedFolderPaths, configWindow.Mode,
                                    configWindow.ModelName, configWindow.HasBlack, configWindow.BlackName);

                    // This generates the previews
                    foreach (var line in _lines)
                    {
                        string first = Directory.EnumerateFiles(line.FolderPath, "*.bmp",
                                          SearchOption.AllDirectories).FirstOrDefault();
                        if (first != null) line.PreparePreview(first);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
                // 'Background' priority is key—it lets the spinner start spinning first.
            }
        }
    }
}