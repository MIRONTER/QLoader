using System;

namespace QSideloader.Utilities;

// Source: https://github.com/LeeCampbell/RxCookbook/blob/master/TPL/IProgress.md
internal class DelegateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value)
    {
        report(value);
    }
}