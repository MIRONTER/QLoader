using System.ComponentModel;
using System.Runtime.CompilerServices;

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

    public bool IsHiddenFromDonation { get; set; }
    public bool IsKnown { get; set; }
    public string DonationStatus { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}