using System.ComponentModel;
using System.Runtime.CompilerServices;
using QSideloader.Utilities;

namespace QSideloader.Models;

public class InstalledApp : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isSelectedDonation;

    public InstalledApp(string name, string packageName, string versionName, int versionCode, bool isKnown,
        bool isHiddenFromDonation, string donationStatus)
    {
        Name = name;
        PackageName = packageName;
        VersionName = versionName;
        VersionCode = versionCode;
        IsKnown = isKnown;
        IsHiddenFromDonation = isHiddenFromDonation;
        DonationStatus = donationStatus;
    }

    public string Name { get; }
    public string PackageName { get; }
    public string VersionName { get; }
    public int VersionCode { get; }

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

    public bool IsHiddenFromDonation { get; }
    public bool IsKnown { get; }
    public string DonationStatus { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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