﻿using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Navigation;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.ViewModels;
using StabilityMatrix.Core.Models.Packages;

namespace StabilityMatrix.Avalonia.Views;

public partial class PackageManagerPage : UserControlBase
{
    public PackageManagerPage()
    {
        InitializeComponent();

        AddHandler(Frame.NavigatedToEvent, OnNavigatedTo, RoutingStrategies.Direct);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Handle navigation events to this page
    /// </summary>
    private void OnNavigatedTo(object? sender, NavigationEventArgs args)
    {
        if (args.Parameter is PackageManagerNavigationOptions { OpenInstallerDialog: true } options)
        {
            var vm = (PackageManagerViewModel)DataContext!;
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await vm.ShowInstallDialog(options.InstallerSelectedPackage);
            });
        }
    }

    public record PackageManagerNavigationOptions
    {
        public bool OpenInstallerDialog { get; init; }

        public BasePackage? InstallerSelectedPackage { get; init; }
    }
}
