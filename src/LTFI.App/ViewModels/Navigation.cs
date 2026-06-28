using System.Threading.Tasks;

namespace LTFI.ViewModels;

/// <summary>A page that reloads its data when navigated to.</summary>
public interface IRefreshable
{
    Task RefreshAsync();
}

/// <summary>One entry in the left sidebar.</summary>
public sealed record NavItem(string Label, ViewModelBase ViewModel);
