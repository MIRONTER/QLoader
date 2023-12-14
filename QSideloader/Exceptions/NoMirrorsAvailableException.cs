using System;
using System.Collections.Generic;
using System.Linq;

namespace QSideloader.Exceptions;

public class NoMirrorsAvailableException(
    bool session,
    IReadOnlyCollection<(string mirrorName, string? message, Exception? error)> excludedMirrors)
    : DownloaderServiceException(session
        ? $"No mirrors available for this session ({excludedMirrors.Count} excluded)\nMirror errors:\n{string.Join("\n\n", excludedMirrors.Select(x => $"___{x.mirrorName}: {x.message ?? x.error?.ToString()}"))
        }"
        : $"No mirrors available for this download ({excludedMirrors.Count} excluded)\nMirror errors:\n{string.Join("\n\n", excludedMirrors.Select(x => $"___{x.mirrorName}: {x.message ?? x.error?.ToString()}"))
        }");