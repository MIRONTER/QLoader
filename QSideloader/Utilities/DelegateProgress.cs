using System;

namespace QSideloader.Utilities;

// Source: https://github.com/LeeCampbell/RxCookbook/blob/master/TPL/IProgress.md
class DelegateProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;
    public DelegateProgress(Action<T> report)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }
    public void Report(T value)
    {
        _report(value);
    }
}