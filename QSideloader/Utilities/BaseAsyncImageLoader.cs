using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;

namespace QSideloader.Utilities;

// Based on https://github.com/AvaloniaUtils/AsyncImageLoader.Avalonia/blob/master/AsyncImageLoader.Avalonia/Loaders/BaseWebImageLoader.cs
public sealed class BaseAsyncImageLoader : IDisposable
{
    private readonly ParametrizedLogger? _logger;
    private readonly bool _shouldDisposeHttpClient;

    public BaseAsyncImageLoader()
        : this(new HttpClient(), true)
    {
    }

    private BaseAsyncImageLoader(HttpClient httpClient, bool disposeHttpClient)
    {
        HttpClient = httpClient;
        _shouldDisposeHttpClient = disposeHttpClient;
        _logger = Logger.TryGet(LogEventLevel.Information, "AsyncImageLoader");
    }

    private HttpClient HttpClient { get; }

    public Task<Bitmap?> ProvideImageAsync(string url) => LoadAsync(url);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private async Task<Bitmap?> LoadAsync(string url)
    {
        var configuredTaskAwaitable = LoadFromLocalAsync(url).ConfigureAwait(false);
        var bitmap1 = await configuredTaskAwaitable;
        if (bitmap1 == null)
        {
            configuredTaskAwaitable = LoadFromInternalAsync(url).ConfigureAwait(false);
            var bitmap2 = await configuredTaskAwaitable;
            if (bitmap2 == null)
            {
                configuredTaskAwaitable = LoadFromGlobalCache(url).ConfigureAwait(false);
                bitmap2 = await configuredTaskAwaitable;
            }

            bitmap1 = bitmap2;
        }

        var bitmap3 = bitmap1;
        if (bitmap3 != null)
            return bitmap3;
        try
        {
            var numArray = await LoadDataFromExternalAsync(url).ConfigureAwait(false);
            if (numArray == null)
                return null;
            using var memoryStream = new MemoryStream(numArray);
            var bitmap = new Bitmap(memoryStream);
            await SaveToGlobalCache(url, numArray).ConfigureAwait(false);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Task<Bitmap?> LoadFromLocalAsync(string url)
    {
        if (!File.Exists(url)) return Task.FromResult<Bitmap?>(null);
        try
        {
            return Task.FromResult<Bitmap?>(new Bitmap(url));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load image from local file: {LocalFile}", url);
        }

        return Task.FromResult<Bitmap?>(null);
    }

    private Task<Bitmap?> LoadFromInternalAsync(string url)
    {
        try
        {
            var uri = url.StartsWith("/") ? new Uri(url, UriKind.Relative) : new Uri(url, UriKind.RelativeOrAbsolute);
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                return Task.FromResult((Bitmap?) null);
            return uri is {IsAbsoluteUri: true, IsFile: true}
                ? Task.FromResult<Bitmap?>(new Bitmap(uri.LocalPath))
                : Task.FromResult<Bitmap?>(new Bitmap(AssetLoader.Open(uri)));
        }
        catch (Exception ex)
        {
            _logger?.Log(this, "Failed to resolve image from request with uri: {RequestUri}\nException: {Exception}",
                url, ex);
            return Task.FromResult((Bitmap?) null);
        }
    }

    private async Task<byte[]?> LoadDataFromExternalAsync(string url)
    {
        try
        {
            return await HttpClient.GetByteArrayAsync(url).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Task<Bitmap?> LoadFromGlobalCache(string url) => Task.FromResult((Bitmap?) null);

    private static Task SaveToGlobalCache(string url, byte[] imageBytes) => Task.CompletedTask;

    ~BaseAsyncImageLoader() => Dispose(false);

    private void Dispose(bool disposing)
    {
        if (!disposing || !_shouldDisposeHttpClient)
            return;
        HttpClient.Dispose();
    }
}