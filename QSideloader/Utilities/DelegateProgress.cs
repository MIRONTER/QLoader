using System;

namespace QSideloader.Utilities;

// Source: https://github.com/LeeCampbell/RxCookbook/blob/master/TPL/IProgress.md
internal class DelegateProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    public DelegateProgress(Action<T> report)
    {
        _report = report;
    }

    public void Report(T value)
    {
        _report(value);
    }
}