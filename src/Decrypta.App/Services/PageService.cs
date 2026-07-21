using System.Windows;
using Wpf.Ui.Abstractions;

namespace Decrypta.App.Services;

/// <summary>
/// Page provider for the NavigationView. Views are created once and cached (so scroll
/// positions, streamed logs and in-flight jobs survive tab switches), and each gets the
/// shell view model as DataContext — the navigation host is a Frame, which does not
/// inherit DataContext from the window.
/// </summary>
public sealed class PageService : INavigationViewPageProvider
{
    private readonly object _dataContext;
    private readonly Dictionary<Type, FrameworkElement> _cache = [];

    public PageService(object dataContext) => _dataContext = dataContext;

    public object? GetPage(Type pageType)
    {
        if (!_cache.TryGetValue(pageType, out FrameworkElement? page))
        {
            page = (FrameworkElement)Activator.CreateInstance(pageType)!;
            page.DataContext = _dataContext;
            _cache[pageType] = page;
        }
        return page;
    }
}
