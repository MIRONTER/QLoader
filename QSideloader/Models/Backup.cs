using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using QSideloader.Utilities;

namespace QSideloader.Models;

public partial class Backup : INotifyPropertyChanged
{
    private bool _isSelected;

    public Backup(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if (!File.Exists(System.IO.Path.Combine(path, ".backup")))
            throw new ArgumentException($"Backup {path} is not valid");
        var dirName = System.IO.Path.GetFileName(path);
        var dateString = DateStringRegex().Match(dirName).Value;
        Name = dirName.Replace(dateString + "_", "");
        // ReSharper disable once StringLiteralTypo
        Date = DateTime.ParseExact(dateString, "yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        Path = path;
        HasApk = Directory.GetFiles(path, "*.apk").Any();
        HasObb = Directory.Exists(System.IO.Path.Combine(path, "obb"));
        HasSharedData = Directory.Exists(System.IO.Path.Combine(path, "data"));
        HasPrivateData = Directory.Exists(System.IO.Path.Combine(path, "data_private"));
    }

    public string Name { get; }
    public DateTime Date { get; }
    public string Path { get; }
    public bool HasApk { get; }
    public bool HasObb { get; }
    public bool HasSharedData { get; }
    public bool HasPrivateData { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public override string ToString()
    {
        // ReSharper disable once StringLiteralTypo
        return Date.ToString("yyyyMMddTHHmmss") + "_" + Name;
    }

    public void Restore()
    {
        Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.Restore, Backup = this});
    }

    [GeneratedRegex("\\d{8}T\\d{6}")]
    private static partial Regex DateStringRegex();
}