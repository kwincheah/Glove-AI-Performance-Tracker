using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;


namespace PerformanceTracker
{
    public partial class LoadConfigWindow : Window
    {
        // Public properties so MainWindow can read the results
        public string Mode => PreToggle.IsChecked == true ? "PRE" : "POST";
        public string FolderPath => ImagePath.Text;
        public string ModelName => ModelPath.Text;

        public string BlackName => BlackModelPath.Text;

        public bool HasBlack => EnableBlack.IsChecked == true ? true : false;





        public LoadConfigWindow()
        {
            InitializeComponent();
        }



        public List<string> SelectedFolderPaths { get; set; } = new List<string>();

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            if (btn == null) return;

            // Use CommonOpenFileDialog for folders, standard OpenFileDialog for .bin files
            if (btn.Name == "ImageBrowse")
            {
                var dialog = new CommonOpenFileDialog
                {
                    IsFolderPicker = true,
                    Multiselect = true,
                    Title = "Select Production Line Folders (Lxx)",
                    InitialDirectory = Directory.Exists(ImagePath.Text) ? ImagePath.Text : @"C:\"
                };

                if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    // Store the list of selected paths
                    SelectedFolderPaths = dialog.FileNames.ToList();

                    // Update the TextBox to show how many folders are selected
                    ImagePath.Text = SelectedFolderPaths.Count == 1
                        ? SelectedFolderPaths[0]
                        : $"{SelectedFolderPaths.Count} Folders Selected";
                }
            }
            else // Logic for ModelBrowse and BlackModelBrowse
            {
                var fileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Vision Model (*.bin)|*.bin",
                    Title = "Select AI Model File",
                    InitialDirectory = AppDomain.CurrentDomain.BaseDirectory
                };

                if (fileDialog.ShowDialog() == true)
                {
                    if (btn.Name == "ModelBrowse") ModelPath.Text = fileDialog.FileName;
                    else if (btn.Name == "BlackModelBrowse") BlackModelPath.Text = fileDialog.FileName;
                }
            }
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            // Validation
            if (SelectedFolderPaths.Count == 0 && !Directory.Exists(ImagePath.Text))
            {
                MessageBox.Show("Please select at least one folder.", "Input Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // If the user manually typed a path or selected just one, ensure it's in the list
            if (SelectedFolderPaths.Count == 0 && Directory.Exists(ImagePath.Text))
            {
                SelectedFolderPaths.Add(ImagePath.Text);
            }

            // Close the window and return 'true' to MainWindow
          
            this.DialogResult = true;
            this.Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            
            this.DialogResult = false;
            this.Close();
        }


    }
}