//using Microsoft.UI.Xaml;
//using Microsoft.UI.Xaml.Media;

namespace QSideloader.Helpers;

public static class XamlHelper
{
    // Source: https://github.com/CommunityToolkit/WindowsCommunityToolkit/issues/2249#issuecomment-439667954
    /*public static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        if (child is null)
            return null;
        T? parent = null;

        var currentParent = VisualTreeHelper.GetParent(child);
        while (currentParent != null)
        {
            if (currentParent is T)
            {
                parent = (T)currentParent;
                break;
            }

            // find the next parent
            currentParent = VisualTreeHelper.GetParent(currentParent);
        }

        return (parent);
    }*/
}