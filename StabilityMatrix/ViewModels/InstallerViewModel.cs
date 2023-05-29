﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StabilityMatrix.Helper;
using StabilityMatrix.Models;
using EventManager = StabilityMatrix.Helper.EventManager;

namespace StabilityMatrix.ViewModels;

public partial class InstallerViewModel : ObservableObject
{
    private readonly ISettingsManager settingsManager;
    private readonly ILogger<InstallerViewModel> logger;
    private readonly IPyRunner pyRunner;
    private readonly IPackageFactory packageFactory;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressBarVisibility))]
    private int progressValue;
    
    [ObservableProperty]
    private BasePackage selectedPackage;
    
    [ObservableProperty]
    private string progressText;
    
    [ObservableProperty]
    private bool isIndeterminate;
    
    [ObservableProperty]
    private Visibility packageInstalledVisibility;
    
    [ObservableProperty]
    private string installButtonText;

    [ObservableProperty]
    private string installPath;

    [ObservableProperty] 
    private string installName;
    
    [ObservableProperty]
    private ObservableCollection<string> availableVersions;

    [ObservableProperty] 
    private ObservableCollection<BasePackage> availablePackages;
    
    public Visibility ProgressBarVisibility => ProgressValue > 0 || IsIndeterminate ? Visibility.Visible : Visibility.Collapsed;

    public InstallerViewModel(ISettingsManager settingsManager, ILogger<InstallerViewModel> logger, IPyRunner pyRunner,
        IPackageFactory packageFactory)
    {
        this.settingsManager = settingsManager;
        this.logger = logger;
        this.pyRunner = pyRunner;
        this.packageFactory = packageFactory;
        
        ProgressText = "";
        InstallButtonText = "Install";
        ProgressValue = 0;
        InstallPath =
            $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}\\StabilityMatrix\\Packages";

        AvailablePackages = new ObservableCollection<BasePackage>(packageFactory.GetAllAvailablePackages());
        if (!AvailablePackages.Any()) return;
        
        SelectedPackage = AvailablePackages[0];
        InstallName = SelectedPackage.Name;
    }

    [RelayCommand]
    private async Task Install()
    {
        await ActuallyInstall();
    }

    public async Task OnLoaded()
    {
        if (SelectedPackage == null)
            return;

        AvailableVersions = new ObservableCollection<string>(await SelectedPackage.GetVersions());
    }

    partial void OnSelectedPackageChanged(BasePackage value)
    {
        InstallName = value.Name;
    }

    private async Task ActuallyInstall()
    {
        var installSuccess = await InstallGitIfNecessary();
        if (!installSuccess)
        {
            logger.LogError("Git installation failed");
            return;
        }

        var version = await DownloadPackage();
        await InstallPackage();

        ProgressText = "Installing dependencies...";
        await pyRunner.Initialize();
        if (!settingsManager.Settings.HasInstalledPip)
        {
            await pyRunner.SetupPip();
            settingsManager.SetHasInstalledPip(true);
        }

        if (!settingsManager.Settings.HasInstalledVenv)
        {
            await pyRunner.InstallPackage("virtualenv");
            settingsManager.SetHasInstalledVenv(true);
        }

        ProgressText = "Done";

        IsIndeterminate = false;
        SelectedPackageOnProgressChanged(this, 100);

        if (settingsManager.Settings.InstalledPackages.FirstOrDefault(x => x.PackageName == SelectedPackage.Name) ==
            null)
        {
            var package = new InstalledPackage
            {
                Name = SelectedPackage.Name,
                Path = SelectedPackage.InstallLocation,
                Id = Guid.NewGuid(),
                PackageName = SelectedPackage.Name,
                PackageVersion = version,
                LaunchCommand = SelectedPackage.LaunchCommand
            };
            settingsManager.AddInstalledPackage(package);
            settingsManager.SetActiveInstalledPackage(package);
        }
    }
    
    private Task<string?> DownloadPackage()
    {
        SelectedPackage.DownloadProgressChanged += SelectedPackageOnProgressChanged;
        SelectedPackage.DownloadComplete += (_, _) => ProgressText = "Download Complete";
        ProgressText = "Downloading package...";
        return SelectedPackage.DownloadPackage();
    }

    private async Task InstallPackage()
    {
        SelectedPackage.InstallProgressChanged += SelectedPackageOnProgressChanged;
        SelectedPackage.InstallComplete += (_, _) => ProgressText = "Install Complete";
        ProgressText = "Installing package...";
        await SelectedPackage.InstallPackage();
    }
    
    private async Task<bool> InstallGitIfNecessary()
    {
        try
        {
            var gitOutput = await ProcessRunner.GetProcessOutputAsync("git", "--version");
            if (gitOutput.Contains("git version 2"))
            {
                return true;
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error running git: ");
        }

        IsIndeterminate = true;
        ProgressText = "Installing Git...";
        using var installProcess =
            ProcessRunner.StartProcess("Assets\\Git-2.40.1-64-bit.exe", "/VERYSILENT /NORESTART");
        installProcess.OutputDataReceived += (sender, args) => { Debug.Write(args.Data); };
        await installProcess.WaitForExitAsync();
        IsIndeterminate = false;

        return installProcess.ExitCode == 0;
    }
    
    private void SelectedPackageOnProgressChanged(object? sender, int progress)
    {
        if (progress == -1)
        {
            IsIndeterminate = true;
        }
        else
        {
            IsIndeterminate = false;
            ProgressValue = progress;
        }
        
        EventManager.Instance.OnGlobalProgressChanged(progress);
    }
    
}
