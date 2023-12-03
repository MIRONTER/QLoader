using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
namespace QSideloader.Utilities; 

// Based on https://github.com/AvaloniaUtils/AsyncImageLoader.Avalonia/blob/master/AsyncImageLoader.Avalonia/ImageLoader.cs
public static class AsyncImageLoader
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(AsyncImageLoader));

    public static readonly AttachedProperty<bool> IsLoadingProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("IsLoading", typeof(AsyncImageLoader));

    static AsyncImageLoader()
    {
        SourceProperty.Changed.AddClassHandler<Image>(OnSourceChanged);
    }

    private static BaseAsyncImageLoader Loader { get; } = new();

	private static ConcurrentDictionary<Image, CancellationTokenSource> _pendingOperations = new();
	private static async void OnSourceChanged(Image sender, AvaloniaPropertyChangedEventArgs args) {
		var url = args.GetNewValue<string?>();

		// Cancel/Add new pending operation
		var cts = _pendingOperations.AddOrUpdate(sender, new CancellationTokenSource(),
			(_, y) =>
			{
				y.Cancel();
				return new CancellationTokenSource();
			});

		if (url == null)
		{
			((ICollection<KeyValuePair<Image, CancellationTokenSource>>)_pendingOperations).Remove(new KeyValuePair<Image, CancellationTokenSource>(sender, cts));
			sender.Source = null;
			return;
		}

		SetIsLoading(sender, true);

		var bitmap = await Task.Run(async () =>
		{
			try
			{
				// A small delay allows to cancel early if the image goes out of screen too fast (eg. scrolling)
				// The Bitmap constructor is expensive and cannot be cancelled
				await Task.Delay(10, cts.Token);

				return await Loader.ProvideImageAsync(url);
			}
			catch (TaskCanceledException)
			{
				return null;
			}
		}, cts.Token);

		if (bitmap != null && !cts.Token.IsCancellationRequested)
			sender.Source = bitmap;

		// "It is not guaranteed to be thread safe by ICollection, but ConcurrentDictionary's implementation is. Additionally, we recently exposed this API for .NET 5 as a public ConcurrentDictionary.TryRemove"
		((ICollection<KeyValuePair<Image, CancellationTokenSource>>)_pendingOperations).Remove(new KeyValuePair<Image, CancellationTokenSource>(sender, cts));
		SetIsLoading(sender, false);
	}

    public static string? GetSource(Image element)
    {
        return element.GetValue(SourceProperty);
    }

    public static void SetSource(Image element, string? value)
    {
        element.SetValue(SourceProperty, value);
    }

    public static bool GetIsLoading(Image element)
    {
        return element.GetValue(IsLoadingProperty);
    }

    private static void SetIsLoading(Image element, bool value)
    {
        element.SetValue(IsLoadingProperty, value);
    }
}