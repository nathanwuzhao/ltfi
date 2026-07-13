using System.Threading.Tasks;

namespace LTFI.ViewModels;

/// <summary>A page that reloads its data when navigated to.</summary>
public interface IRefreshable
{
    Task RefreshAsync();
}

/// <summary>
/// One entry in the navigation rail. <see cref="Code"/> is the short 3-letter label shown in the
/// 46px icon rail (CMD, TDY, …); <see cref="Label"/> is the full name shown elsewhere.
/// </summary>
public sealed record NavItem(string Code, string Label, ViewModelBase ViewModel);
