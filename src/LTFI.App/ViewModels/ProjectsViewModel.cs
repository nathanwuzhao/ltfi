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
    private readonly IMilestoneService _milestoneService;
    private readonly List<Project> _allProjects = [];
    private bool _suppressSelectionLoad;

    // A save that's blocked on the active-project limit, awaiting a pause/kill decision.
    private ProjectDraft? _pendingDraft;
    private Guid? _pendingUpdateId;

    public ObservableCollection<Project> Projects { get; } = [];

    /// <summary>Active projects offered for pause/kill when the active limit is hit.</summary>
    public ObservableCollection<Project> ActiveProjectsToResolve { get; } = [];

    public ObservableCollection<Milestone> Milestones { get; } = [];

    public Array Statuses { get; } = Enum.GetValues<ProjectStatus>();

    public string Header => "Projects";

    /// <summary>When false (default), Completed/Killed projects are hidden from the list.</summary>
    [ObservableProperty]
    private bool showArchived;

    [ObservableProperty]
    private bool isResolvingLimit;

    [ObservableProperty]
    private string newMilestoneTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddMilestoneCommand))]
    [NotifyPropertyChangedFor(nameof(CanManageMilestones))]
    private Project? selectedProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    [NotifyPropertyChangedFor(nameof(CanManageMilestones))]
    [NotifyCanExecuteChangedFor(nameof(AddMilestoneCommand))]
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

    public ProjectsViewModel(IProjectService projectService, IMilestoneService milestoneService)
    {
        _projectService = projectService;
        _milestoneService = milestoneService;
        BeginNewProject();
    }

    public string EditorTitle => IsCreatingNew ? "New Project" : "Edit Project";

    public string SaveButtonText => IsCreatingNew ? "Create Project" : "Save Changes";

    public bool CanManageMilestones => !IsCreatingNew && SelectedProject is not null;

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

        _pendingDraft = draft;
        _pendingUpdateId = IsCreatingNew ? null : SelectedProject?.Id;
        await ExecutePendingSaveAsync();
    }

    private async Task ExecutePendingSaveAsync()
    {
        if (_pendingDraft is not { } draft)
        {
            return;
        }

        try
        {
            if (_pendingUpdateId is { } id)
            {
                await _projectService.UpdateAsync(id, draft);
                await RefreshAsync();
                SelectById(id);
                FeedbackMessage = "Changes saved.";
            }
            else
            {
                var created = await _projectService.CreateAsync(draft);
                await RefreshAsync();
                SelectById(created.Id);
                FeedbackMessage = "Project created.";
            }

            _pendingDraft = null;
            IsResolvingLimit = false;
        }
        catch (ActiveProjectLimitException)
        {
            // Offer the user a way to make room rather than just failing.
            ActiveProjectsToResolve.Clear();
            foreach (var project in _allProjects.Where(p => p.Status == ProjectStatus.Active))
            {
                ActiveProjectsToResolve.Add(project);
            }

            IsResolvingLimit = true;
            FeedbackMessage = $"You already have {ProjectPolicy.MaxActiveProjects} active projects. Pause or kill one to make room.";
        }
        catch (Exception ex)
        {
            _pendingDraft = null;
            FeedbackMessage = ex.Message;
        }
    }

    [RelayCommand]
    private Task PauseToMakeRoomAsync(Project? project) => FreeUpAsync(project, ProjectStatus.Paused);

    [RelayCommand]
    private Task KillToMakeRoomAsync(Project? project) => FreeUpAsync(project, ProjectStatus.Killed);

    [RelayCommand]
    private void CancelActivation()
    {
        _pendingDraft = null;
        IsResolvingLimit = false;
        FeedbackMessage = "Activation canceled.";
    }

    private async Task FreeUpAsync(Project? project, ProjectStatus status)
    {
        if (project is null)
        {
            return;
        }

        try
        {
            await _projectService.UpdateAsync(project.Id, new ProjectDraft
            {
                Title = project.Title,
                Description = project.Description,
                Status = status,
                DoneCondition = project.DoneCondition,
                TargetDate = project.TargetDate
            });
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
            return;
        }

        // Refresh the active list and retry the blocked activation.
        await RefreshAsync();
        await ExecutePendingSaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanManageMilestones))]
    private async Task AddMilestoneAsync()
    {
        if (SelectedProject is null || string.IsNullOrWhiteSpace(NewMilestoneTitle))
        {
            return;
        }

        try
        {
            await _milestoneService.CreateAsync(SelectedProject.Id, new MilestoneDraft { Title = NewMilestoneTitle });
            NewMilestoneTitle = string.Empty;
            await LoadMilestonesAsync(SelectedProject.Id);
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleMilestoneAsync(Milestone? milestone)
    {
        if (milestone is null || SelectedProject is null)
        {
            return;
        }

        var next = milestone.Status == MilestoneStatus.Completed
            ? MilestoneStatus.Planned
            : MilestoneStatus.Completed;
        await _milestoneService.SetStatusAsync(milestone.Id, next);
        await LoadMilestonesAsync(SelectedProject.Id);
    }

    [RelayCommand]
    private async Task DeleteMilestoneAsync(Milestone? milestone)
    {
        if (milestone is null || SelectedProject is null)
        {
            return;
        }

        await _milestoneService.DeleteAsync(milestone.Id);
        await LoadMilestonesAsync(SelectedProject.Id);
    }

    private async Task LoadMilestonesAsync(Guid projectId)
    {
        var milestones = await _milestoneService.GetByProjectAsync(projectId);
        Milestones.Clear();
        foreach (var milestone in milestones)
        {
            Milestones.Add(milestone);
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
        NewMilestoneTitle = string.Empty;
        FeedbackMessage = string.Empty;
        Milestones.Clear();
    }

    private void LoadFromProject(Project project)
    {
        IsCreatingNew = false;
        DraftTitle = project.Title;
        DraftDescription = project.Description ?? string.Empty;
        NewMilestoneTitle = string.Empty;
        _ = LoadMilestonesAsync(project.Id);
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
