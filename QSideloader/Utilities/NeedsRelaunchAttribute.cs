namespace QSideloader.Utilities;

/// <summary>
/// Attribute for marking settings that require relaunch to apply.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Property)]
public class NeedsRelaunchAttribute : System.Attribute
{
}