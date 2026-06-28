using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;

namespace LTFI.ViewModels;

/// <summary>Create, view, edit, and delete projects (Phase 1 acceptance).</summary>
public partial class ProjectsViewModel : ViewModelBase, IRefreshable
{
    private readonly IProjectService _projectService;
    private readonly List<Project> _allProjects = [];
    private bool _suppressSelectionLoad;

    public ObservableCollection<Project> Projects { get; } = [];

    public Array Statuses { get; } = Enum.GetValues<ProjectStatus>();

    public string Header => "Projects";

    /// <summary>When false (default), Completed/Killed projects are hidden from the list.</summary>
    [ObservableProperty]
    private bool showArchived;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProjectCommand))]
    private Project? selectedProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private bool isCreatingNew = true;

    [ObservableProperty]
    private string draftTitle = string.Empty;

    [ObservableProperty]
    private string draftDescription = string.Empty;

    [ObservableProperty]
    private ProjectStatus selectedStatus = ProjectStatus.Active;

    [ObservableProperty]
    private string draftDoneCondition = string.Empty;

    [ObservableProperty]
    private string draftTargetDateText = string.Empty;

    [ObservableProperty]
    private string feedbackMessage = string.Empty;

    public ProjectsViewModel(IProjectService projectService)
    {
        _projectService = projectService;
        BeginNewProject();
    }

    public string EditorTitle => IsCreatingNew ? "New Project" : "Edit Project";

    public string SaveButtonText => IsCreatingNew ? "Create Project" : "Save Changes";

    public async Task RefreshAsync()
    {
        var projects = await _projectService.GetAllAsync();
        _allProjects.Clear();
        _allProjects.AddRange(projects);
        ApplyFilter();
    }

    partial void OnShowArchivedChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var selectedId = SelectedProject?.Id;

        _suppressSelectionLoad = true;
        Projects.Clear();
        foreach (var project in _allProjects.Where(p => ShowArchived || !p.IsArchived))
        {
            Projects.Add(project);
        }

        SelectedProject = Projects.FirstOrDefault(p => p.Id == selectedId);
        _suppressSelectionLoad = false;

        DeleteProjectCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NewProject() => BeginNewProject();

    [RelayCommand]
    private async Task SaveProjectAsync()
    {
        if (!TryBuildDraft(out var draft))
        {
            return;
        }

        try
        {
            if (IsCreatingNew)
            {
                var created = await _projectService.CreateAsync(draft);
                await RefreshAsync();
                SelectById(created.Id);
                FeedbackMessage = "Project created.";
            }
            else if (SelectedProject is not null)
            {
                var id = SelectedProject.Id;
                await _projectService.UpdateAsync(id, draft);
                await RefreshAsync();
                SelectById(id);
                FeedbackMessage = "Changes saved.";
            }
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteProject))]
    private async Task DeleteProjectAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        try
        {
            await _projectService.DeleteAsync(SelectedProject.Id);
            await RefreshAsync();
            BeginNewProject();
            FeedbackMessage = "Project deleted.";
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    private bool CanDeleteProject() => SelectedProject is not null;

    partial void OnSelectedProjectChanged(Project? value)
    {
        DeleteProjectCommand.NotifyCanExecuteChanged();

        if (_suppressSelectionLoad || value is null)
        {
            return;
        }

        LoadFromProject(value);
    }

    private void SelectById(Guid id) =>
        SelectedProject = Projects.FirstOrDefault(p => p.Id == id);

    private void BeginNewProject()
    {
        _suppressSelectionLoad = true;
        SelectedProject = null;
        _suppressSelectionLoad = false;

        IsCreatingNew = true;
        DraftTitle = string.Empty;
        DraftDescription = string.Empty;
        SelectedStatus = ProjectStatus.Active;
        DraftDoneCondition = string.Empty;
        DraftTargetDateText = string.Empty;
        FeedbackMessage = string.Empty;
    }

    private void LoadFromProject(Project project)
    {
        IsCreatingNew = false;
        DraftTitle = project.Title;
        DraftDescription = project.Description ?? string.Empty;
        SelectedStatus = project.Status;
        DraftDoneCondition = project.DoneCondition ?? string.Empty;
        DraftTargetDateText = project.TargetDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        FeedbackMessage = string.Empty;
    }

    private bool TryBuildDraft(out ProjectDraft draft)
    {
        draft = new ProjectDraft();

        if (string.IsNullOrWhiteSpace(DraftTitle))
        {
            FeedbackMessage = "Title is required.";
            return false;
        }

        DateTimeOffset? targetDate = null;
        if (!string.IsNullOrWhiteSpace(DraftTargetDateText))
        {
            if (!DateTimeOffset.TryParse(DraftTargetDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                FeedbackMessage = "Target date must be empty or a valid date such as 2026-06-30.";
                return false;
            }

            targetDate = parsed;
        }

        draft = new ProjectDraft
        {
            Title = DraftTitle,
            Description = DraftDescription,
            Status = SelectedStatus,
            DoneCondition = DraftDoneCondition,
            TargetDate = targetDate
        };

        return true;
    }
}
