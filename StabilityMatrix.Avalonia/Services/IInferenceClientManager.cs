﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using DynamicData.Binding;
using StabilityMatrix.Core.Inference;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;

namespace StabilityMatrix.Avalonia.Services;

public interface IInferenceClientManager
    : IDisposable,
        INotifyPropertyChanged,
        INotifyPropertyChanging
{
    ComfyClient? Client { get; set; }

    /// <summary>
    /// Whether the client is connected
    /// </summary>
    [MemberNotNullWhen(true, nameof(Client))]
    bool IsConnected { get; }

    /// <summary>
    /// Whether the client is connecting
    /// </summary>
    bool IsConnecting { get; }

    /// <summary>
    /// Whether the user can initiate a connection
    /// </summary>
    bool CanUserConnect { get; }

    /// <summary>
    /// Whether the user can initiate a disconnection
    /// </summary>
    bool CanUserDisconnect { get; }

    IObservableCollection<HybridModelFile> Models { get; }
    IObservableCollection<HybridModelFile> VaeModels { get; }
    IObservableCollection<ComfySampler> Samplers { get; }
    IObservableCollection<ComfyUpscaler> Upscalers { get; }
    IObservableCollection<ComfyScheduler> Schedulers { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task ConnectAsync(PackagePair packagePair, CancellationToken cancellationToken = default);

    Task CloseAsync();
}
