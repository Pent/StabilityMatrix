﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using NLog;
using Refit;
using SkiaSharp;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Helpers;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Inference;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Inference;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Comfy.WebSocketData;

namespace StabilityMatrix.Avalonia.ViewModels.Base;

/// <summary>
/// Abstract base class for tab view models that generate images using ClientManager.
/// This includes a progress reporter, image output view model, and generation virtual methods.
/// </summary>
[SuppressMessage("ReSharper", "VirtualMemberNeverOverridden.Global")]
public abstract partial class InferenceGenerationViewModelBase
    : InferenceTabViewModelBase,
        IImageGalleryComponent
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly INotificationService notificationService;

    [JsonPropertyName("ImageGallery")]
    public ImageGalleryCardViewModel ImageGalleryCardViewModel { get; }

    [JsonIgnore]
    public ImageFolderCardViewModel ImageFolderCardViewModel { get; }

    [JsonIgnore]
    public ProgressViewModel OutputProgress { get; } = new();

    [JsonIgnore]
    public IInferenceClientManager ClientManager { get; }

    /// <inheritdoc />
    protected InferenceGenerationViewModelBase(
        ServiceManager<ViewModelBase> vmFactory,
        IInferenceClientManager inferenceClientManager,
        INotificationService notificationService
    )
    {
        this.notificationService = notificationService;

        ClientManager = inferenceClientManager;

        ImageGalleryCardViewModel = vmFactory.Get<ImageGalleryCardViewModel>();
        ImageFolderCardViewModel = vmFactory.Get<ImageFolderCardViewModel>();

        GenerateImageCommand.WithConditionalNotificationErrorHandler(notificationService);
    }

    /// <summary>
    /// Builds the image generation prompt
    /// </summary>
    protected virtual void BuildPrompt(BuildPromptEventArgs args) { }

    /// <summary>
    /// Runs a generation task
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if args.Parameters or args.Project are null</exception>
    protected async Task RunGeneration(
        ImageGenerationEventArgs args,
        CancellationToken cancellationToken
    )
    {
        var client = args.Client;
        var nodes = args.Nodes;

        // Checks
        if (args.Parameters is null)
            throw new InvalidOperationException("Parameters is null");
        if (args.Project is null)
            throw new InvalidOperationException("Project is null");
        if (args.OutputNodeNames.Count == 0)
            throw new InvalidOperationException("OutputNodeNames is empty");
        if (client.OutputImagesDir is null)
            throw new InvalidOperationException("OutputImagesDir is null");

        // Connect preview image handler
        client.PreviewImageReceived += OnPreviewImageReceived;

        ComfyTask? promptTask = null;

        try
        {
            // Register to interrupt if user cancels
            cancellationToken.Register(() =>
            {
                Logger.Info("Cancelling prompt");
                client
                    .InterruptPromptAsync(new CancellationTokenSource(5000).Token)
                    .SafeFireAndForget();
            });

            try
            {
                promptTask = await client.QueuePromptAsync(nodes, cancellationToken);
            }
            catch (ApiException e)
            {
                Logger.Warn(e, "Api exception while queuing prompt");
                await DialogHelper.CreateApiExceptionDialog(e, "Api Error").ShowAsync();
                return;
            }

            // Register progress handler
            promptTask.ProgressUpdate += OnProgressUpdateReceived;

            // Wait for prompt to finish
            await promptTask.Task.WaitAsync(cancellationToken);
            Logger.Trace($"Prompt task {promptTask.Id} finished");

            // Get output images
            var imageOutputs = await client.GetImagesForExecutedPromptAsync(
                promptTask.Id,
                cancellationToken
            );

            ImageGalleryCardViewModel.ImageSources.Clear();

            if (
                !imageOutputs.TryGetValue(args.OutputNodeNames[0], out var images) || images is null
            )
            {
                // No images match
                notificationService.Show("No output", "Did not receive any output images");
                return;
            }

            await ProcessOutputImages(images, args);
        }
        finally
        {
            // Disconnect progress handler
            client.PreviewImageReceived -= OnPreviewImageReceived;

            // Clear progress
            OutputProgress.Value = 0;
            OutputProgress.Text = "";
            ImageGalleryCardViewModel.PreviewImage?.Dispose();
            ImageGalleryCardViewModel.PreviewImage = null;
            ImageGalleryCardViewModel.IsPreviewOverlayEnabled = false;

            // Cleanup tasks
            promptTask?.Dispose();
        }
    }

    /// <summary>
    /// Handles image output metadata for generation runs
    /// </summary>
    private async Task ProcessOutputImages(
        IEnumerable<ComfyImage> images,
        ImageGenerationEventArgs args
    )
    {
        // Write metadata to images
        var outputImages = new List<ImageSource>();
        foreach (
            var filePath in images.Select(image => image.ToFilePath(args.Client.OutputImagesDir!))
        )
        {
            var bytesWithMetadata = PngDataHelper.AddMetadata(
                await filePath.ReadAllBytesAsync(),
                args.Parameters!,
                args.Project!
            );

            await using (var outputStream = filePath.Info.OpenWrite())
            {
                await outputStream.WriteAsync(bytesWithMetadata);
                await outputStream.FlushAsync();
            }

            outputImages.Add(new ImageSource(filePath));

            EventManager.Instance.OnImageFileAdded(filePath);
        }

        // Download all images to make grid, if multiple
        if (outputImages.Count > 1)
        {
            var loadedImages = outputImages
                .Select(i => SKImage.FromEncodedData(i.LocalFile?.Info.OpenRead()))
                .ToImmutableArray();

            var grid = ImageProcessor.CreateImageGrid(loadedImages);
            var gridBytes = grid.Encode().ToArray();
            var gridBytesWithMetadata = PngDataHelper.AddMetadata(
                gridBytes,
                args.Parameters!,
                args.Project!
            );

            // Save to disk
            var lastName = outputImages.Last().LocalFile?.Info.Name;
            var gridPath = args.Client.OutputImagesDir!.JoinFile($"grid-{lastName}");

            await using (var fileStream = gridPath.Info.OpenWrite())
            {
                await fileStream.WriteAsync(gridBytesWithMetadata);
            }

            // Insert to start of images
            var gridImage = new ImageSource(gridPath);
            // Preload
            await gridImage.GetBitmapAsync();
            ImageGalleryCardViewModel.ImageSources.Add(gridImage);

            EventManager.Instance.OnImageFileAdded(gridPath);
        }

        // Add rest of images
        foreach (var img in outputImages)
        {
            // Preload
            await img.GetBitmapAsync();
            ImageGalleryCardViewModel.ImageSources.Add(img);
        }
    }

    /// <summary>
    /// Implementation for Generate Image
    /// </summary>
    protected virtual Task GenerateImageImpl(
        GenerateOverrides overrides,
        CancellationToken cancellationToken
    )
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Command for the Generate Image button
    /// </summary>
    /// <param name="options">Optional overrides (side buttons)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [RelayCommand(IncludeCancelCommand = true, FlowExceptionsToTaskScheduler = true)]
    private async Task GenerateImage(
        GenerateFlags options = default,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var overrides = GenerateOverrides.FromFlags(options);

            await GenerateImageImpl(overrides, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Logger.Debug($"Image Generation Canceled");
        }
    }

    /// <summary>
    /// Handles the preview image received event from the websocket.
    /// Updates the preview image in the image gallery.
    /// </summary>
    protected virtual void OnPreviewImageReceived(object? sender, ComfyWebSocketImageData args)
    {
        ImageGalleryCardViewModel.SetPreviewImage(args.ImageBytes);
    }

    /// <summary>
    /// Handles the progress update received event from the websocket.
    /// Updates the progress view model.
    /// </summary>
    protected virtual void OnProgressUpdateReceived(
        object? sender,
        ComfyProgressUpdateEventArgs args
    )
    {
        Dispatcher.UIThread.Post(() =>
        {
            OutputProgress.Value = args.Value;
            OutputProgress.Maximum = args.Maximum;
            OutputProgress.IsIndeterminate = false;

            OutputProgress.Text =
                $"({args.Value} / {args.Maximum})"
                + (args.RunningNode != null ? $" {args.RunningNode}" : "");
        });
    }

    public class ImageGenerationEventArgs : EventArgs
    {
        public required ComfyClient Client { get; init; }
        public required NodeDictionary Nodes { get; init; }
        public required IReadOnlyList<string> OutputNodeNames { get; init; }
        public GenerationParameters? Parameters { get; set; }
        public InferenceProjectDocument? Project { get; set; }
    }

    public class BuildPromptEventArgs : EventArgs
    {
        public ComfyNodeBuilder Builder { get; } = new();
        public GenerateOverrides Overrides { get; set; } = new();
    }
}
