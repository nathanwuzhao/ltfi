namespace LTFI.ViewModels;

/// <summary>A simple "coming in a later phase" page used for not-yet-built sections.</summary>
public class PlaceholderViewModel(string title, string message) : ViewModelBase
{
    public string Title { get; } = title;

    public string Message { get; } = message;
}
