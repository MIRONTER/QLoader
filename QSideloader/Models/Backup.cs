using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Serilog;

namespace QSideloader.Models;

public class Backup : INotifyPropertyChanged
{
    private bool _isSelected;

    public Backup(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if (!File.Exists(System.IO.Path.Combine(path, ".backup")))
        {
            throw new ArgumentException($"Backup {path} is not valid");
        }
        var dirName = System.IO.Path.GetFileName(path);
        var dateString = Regex.Match(dirName, @"\d{8}T\d{6}").Value;
        Name = dirName.Replace(dateString + "_", "");
        Date = DateTime.ParseExact(dateString, "yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        Path = path;
        ContainsApk = Directory.GetFiles(path, "*.apk").Any();
        ContainsObb = Directory.Exists(System.IO.Path.Combine(path, "obb"));
        ContainsSharedData = Directory.Exists(System.IO.Path.Combine(path, "data"));
        ContainsPrivateData = Directory.Exists(System.IO.Path.Combine(path, "data_private"));
    }

    public string Name { get; }
    public DateTime Date { get; }
    public string Path { get; }
    public bool ContainsApk { get; }
    public bool ContainsObb { get; }
    public bool ContainsSharedData { get; }
    public bool ContainsPrivateData { get; }
    
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
        return Date.ToString("yyyyMMddTHHmmss") + "_" + Name;
    }
}