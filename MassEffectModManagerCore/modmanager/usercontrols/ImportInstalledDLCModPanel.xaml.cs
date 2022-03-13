﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using IniParser.Model;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using ME3TweaksCore.GameFilesystem;
using ME3TweaksCore.Helpers;
using ME3TweaksCore.Services.ThirdPartyModIdentification;
using ME3TweaksCore.Targets;
using ME3TweaksCoreWPF;
using ME3TweaksCoreWPF.Targets;
using ME3TweaksCoreWPF.UI;
using ME3TweaksModManager.modmanager.diagnostics;
using ME3TweaksModManager.modmanager.helpers;
using ME3TweaksModManager.modmanager.localizations;
using ME3TweaksModManager.modmanager.objects.mod;
using ME3TweaksModManager.ui;
using Microsoft.AppCenter.Analytics;

namespace ME3TweaksModManager.modmanager.usercontrols
{
    /// <summary>
    /// Interaction logic for ImportInstalledDLCModPanel.xaml
    /// </summary>
    public partial class ImportInstalledDLCModPanel : MMBusyPanelBase
    {
        public GameTargetWPF SelectedTarget { get; set; }
        public InstalledDLCMod SelectedDLCFolder { get; set; }
        public ObservableCollectionExtended<InstalledDLCMod> InstalledDLCModsForGame { get; } = new();
        public ObservableCollectionExtended<GameTargetWPF> InstallationTargets { get; } = new();
        public ImportInstalledDLCModPanel()
        {
            LoadCommands();
        }
        public string ModSiteText { get; set; }
        public string ModNameText { get; set; }

        public bool OperationInProgress { get; set; }
        public bool ListEnabled { get; set; } = true;
        public bool CurrentModInTPMI { get; set; } = true; // until an item is selected, don't show the uncataloged item
        public ICommand ImportSelectedDLCFolderCommand { get; set; }
        public ICommand CloseCommand { get; set; }
        private void LoadCommands()
        {
            CloseCommand = new GenericCommand(ClosePanel, CanClosePanel);
            ImportSelectedDLCFolderCommand = new GenericCommand(ImportSelectedFolder, CanImportSelectedFolder);
        }

        private bool CanImportSelectedFolder() => SelectedDLCFolder != null && !string.IsNullOrWhiteSpace(ModNameText) && !SelectedTarget.TextureModded;

        private void ImportSelectedFolder()
        {
            //Check destination path
            var destinationName = M3Utilities.SanitizePath(ModNameText);
            if (string.IsNullOrWhiteSpace(destinationName))
            {
                //cannot use this name
                M3Log.Error(@"Invalid mod name: " + ModNameText);
                M3L.ShowDialog(mainwindow, M3L.GetString(M3L.string_dialog_invalidModNameWillResolveToNothing), M3L.GetString(M3L.string_invalidModName), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            //Check free space.
            var sourceDir = Path.Combine(M3Directories.GetDLCPath(SelectedTarget), SelectedDLCFolder.DLCFolderName);
            var library = M3Utilities.GetModDirectoryForGame(SelectedTarget.Game);
            if (M3Utilities.DriveFreeBytes(library, out var freeBytes))
            {
                //Check enough space
                var sourceSize = M3Utilities.GetSizeOfDirectory(sourceDir);
                if (sourceSize > (long)freeBytes)
                {
                    //Not enough space
                    M3Log.Error($@"Not enough disk space to import mod. Required space: {FileSize.FormatSize(sourceSize)}, available space: {FileSize.FormatSize(freeBytes)}");
                    M3L.ShowDialog(mainwindow, M3L.GetString(M3L.string_interp_insufficientDiskSpaceToImport, Path.GetPathRoot(library), FileSize.FormatSize(sourceSize), FileSize.FormatSize(freeBytes)), M3L.GetString(M3L.string_insufficientFreeDiskSpace), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            //Check directory doesn't exist already
            var outDir = Path.Combine(library, destinationName);
            if (Directory.Exists(outDir))
            {
                var okToDelete = M3L.ShowDialog(mainwindow, M3L.GetString(M3L.string_interp_dialog_importingWillDeleteExistingMod, outDir), M3L.GetString(M3L.string_sameNamedModInLibrary), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (okToDelete == MessageBoxResult.No)
                {
                    return; //cancel
                }

                try
                {
                    M3Utilities.DeleteFilesAndFoldersRecursively(outDir);
                }
                catch (Exception e)
                {
                    M3L.ShowDialog(mainwindow, M3L.GetString(M3L.string_interp_couldNotDeleteExistingModDirectory, e.Message), M3L.GetString(M3L.string_errorDeletingModFolder), MessageBoxButton.OK, MessageBoxImage.Error);
                    return; //abort
                }
            }

            NamedBackgroundWorker nbw = new NamedBackgroundWorker(@"GameDLCModImporter");
            nbw.DoWork += ImportDLCFolder_BackgroundThread;
            nbw.RunWorkerCompleted += (a, b) =>
            {
                if (b.Error != null)
                {
                    M3Log.Error($@"Exception occurred in {nbw.Name} thread: {b.Error.Message}");
                }
                else
                {

                    if (b.Error == null && b.Result != null)
                    {
                        Analytics.TrackEvent(@"Imported a mod from game installation", new Dictionary<string, string>()
                        {
                            {@"Game", SelectedTarget.Game.ToString()},
                            {@"Folder", SelectedDLCFolder.DLCFolderName}
                        });
                    }

                    OperationInProgress = false;
                    if (b.Error == null && b.Result != null)
                    {
                        if (b.Result is Mod m)
                        {
                            Result.ModToHighlightOnReload = m;
                            Result.ReloadMods = true;
                        }
                        ClosePanel(); //avoid accessing b.Result if error occurred
                    }
                }
            };
            nbw.RunWorkerAsync();
        }

        private void ImportDLCFolder_BackgroundThread(object sender, DoWorkEventArgs e)
        {
            OperationInProgress = true;
            var sourceDir = Path.Combine(M3Directories.GetDLCPath(SelectedTarget), SelectedDLCFolder.DLCFolderName);
            // Check for MEMI, we will not allow importing files with MEMI
            foreach (var file in Directory.GetFiles(sourceDir, @"*.*", SearchOption.AllDirectories))
            {
                if (file.RepresentsPackageFilePath() && M3Utilities.HasALOTMarker(file))
                {
                    M3Log.Error($@"Found a file marked as texture modded: {file}. These files cannot be imported into mod manager");
                    Application.Current.Dispatcher.Invoke(delegate
                    {
                        M3L.ShowDialog(window, M3L.GetString(M3L.string_dialog_cannotImportModTextureMarkersFound), M3L.GetString(M3L.string_cannotImportMod), MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                    return;
                }
            }



            var library = M3Utilities.GetModDirectoryForGame(SelectedTarget.Game);
            var destinationName = M3Utilities.SanitizePath(ModNameText);
            var modFolder = Path.Combine(library, destinationName);
            var copyDestination = Path.Combine(modFolder, SelectedDLCFolder.DLCFolderName);
            var outInfo = Directory.CreateDirectory(copyDestination);
            M3Log.Information($@"Importing mod: {sourceDir} -> {copyDestination}");

            int numToDo = 0;
            int numDone = 0;

            void totalItemToCopyCallback(int total)
            {
                numToDo = total;
                ProgressBarMax = total;
            }

            void fileCopiedCallback()
            {
                numDone++;
                ProgressBarValue = numDone;
            }

            CopyDir.CopyAll_ProgressBar(new DirectoryInfo(sourceDir), outInfo, totalItemToCopyCallback, fileCopiedCallback);

            //Write a moddesc
            IniData ini = new IniData();
            ini[@"ModManager"][@"cmmver"] = App.HighestSupportedModDesc.ToString(CultureInfo.InvariantCulture); //prevent commas
            ini[@"ModInfo"][@"game"] = SelectedTarget.Game.ToString();
            ini[@"ModInfo"][@"modname"] = ModNameText;
            ini[@"ModInfo"][@"moddev"] = M3L.GetString(M3L.string_importedFromGame);
            ini[@"ModInfo"][@"moddesc"] = M3L.GetString(M3L.string_defaultDescriptionForImportedMod, SelectedTarget.Game.ToGameName(), DateTime.Now);
            ini[@"ModInfo"][@"modver"] = M3L.GetString(M3L.string_unknown);
            ini[@"ModInfo"][@"unofficial"] = @"true";
            ini[@"ModInfo"][@"importedby"] = App.BuildNumber.ToString();
            ini[@"CUSTOMDLC"][@"sourcedirs"] = SelectedDLCFolder.DLCFolderName;
            ini[@"CUSTOMDLC"][@"destdirs"] = SelectedDLCFolder.DLCFolderName;


            var moddescPath = Path.Combine(modFolder, @"moddesc.ini");
            File.WriteAllText(moddescPath, ini.ToString());

            //Generate and load mod
            var m = new Mod(moddescPath, MEGame.ME3);
            e.Result = m;
            M3Log.Information(@"Mod import complete.");
            Analytics.TrackEvent(@"Imported already installed mod", new Dictionary<string, string>()
            {
                {@"Mod name", m.ModName},
                {@"Game", SelectedTarget.Game.ToString()},
                {@"Folder name", SelectedDLCFolder.DLCFolderName}
            });

            if (!CurrentModInTPMI)
            {
                //Submit telemetry to ME3Tweaks
                try
                {
                    TPMITelemetrySubmissionForm.TelemetryPackage tp = TPMITelemetrySubmissionForm.GetTelemetryPackageForDLC(SelectedTarget.Game,
                        M3Directories.GetDLCPath(SelectedTarget),
                        SelectedDLCFolder.DLCFolderName,
                        SelectedDLCFolder.DLCFolderName, //same as foldername as this is already installed
                        ModNameText,
                        @"N/A",
                        ModSiteText,
                        null
                    );

                    tp.SubmitPackage();
                }
                catch (Exception ex)
                {
                    M3Log.Error(@"Cannot submit telemetry: " + ex.Message);
                }
            }
        }

        public int ProgressBarValue { get; set; }

        public int ProgressBarMax { get; set; } = 100; //Default so it doesn't appear full on start

        public void OnSelectedDLCFolderChanged()
        {
            ModSiteText = "";
            if (SelectedDLCFolder != null && SelectedTarget != null && !SelectedTarget.TextureModded)
            {
                TPMIService.TryGetModInfo(MEGame.ME3, SelectedDLCFolder.DLCFolderName, out var tpmi);
                CurrentModInTPMI = tpmi != null;
                if (CurrentModInTPMI)
                {
                    ModNameText = tpmi.modname;
                }
                else
                {
                    ModNameText = "";
                }

                //Check ALOT
            }
            else
            {
                CurrentModInTPMI = true; //Hide UI
            }
        }

        public void OnCurrentModInTPMIChanged()
        {
            // Disable clicking while it's animating.
            ListEnabled = false;
            ClipperHelper.ShowHideVerticalContent(TPMI_Panel, !CurrentModInTPMI, completionDelegate: () => ListEnabled = true);
        }

        public void OnSelectedTargetChanged()
        {
            if (SelectedTarget != null)
            {
                SelectedTarget.PopulateDLCMods(false, modNamePrefersTPMI: true);
                InstalledDLCModsForGame.ReplaceAll(SelectedTarget.UIInstalledDLCMods.OrderBy(x => x.InstalledByManagedSolution));
            }
            else
            {
                InstalledDLCModsForGame.ClearEx();
            }
        }

        private bool CanClosePanel() => !OperationInProgress;

        private void ClosePanel() => OnClosing(DataEventArgs.Empty);

        public override void HandleKeyPress(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && CanClosePanel())
            {
                ClosePanel();
            }
        }

        public override void OnPanelVisible()
        {
            InitializeComponent();
            InstallationTargets.ReplaceAll(mainwindow.InstallationTargets.Where(x => x.Selectable && x.Game != MEGame.LELauncher));
            SelectedTarget = InstallationTargets.FirstOrDefault();
        }
    }
}
