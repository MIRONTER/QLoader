using System.ComponentModel;
using System.Runtime.CompilerServices;
using QSideloader.Utilities;

namespace QSideloader.Models;

public class InstalledApp(
    string name,
    string packageName,
    string versionName,
    int versionCode,
    bool isKnown,
    bool isHiddenFromDonation,
    string donationStatus)
    : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isSelectedDonation;

    public string Name { get; } = name;
    public string PackageName { get; } = packageName;
    public string VersionName { get; } = versionName;
    public int VersionCode { get; } = versionCode;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelectedDonation
    {
        get => _isSelectedDonation;
        set
        {
            _isSelectedDonation = value;
            OnPropertyChanged();
        }
    }

    public bool IsHiddenFromDonation { get; } = isHiddenFromDonation;
    public bool IsKnown { get; } = isKnown;
    public string DonationStatus { get; } = donationStatus;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }

    public void PullAndUpload()
    {
        Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.PullAndUpload, App = this});
    }

    public void Uninstall()
    {
        Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.Uninstall, App = this});
    }

    public void Extract(string path)
    {
        Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.Extract, App = this, Path = path});
    }
}