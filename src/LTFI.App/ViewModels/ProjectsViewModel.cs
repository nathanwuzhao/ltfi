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

/// <summary>One row of the AREAS sub-panel; <see cref="EditName"/> is the rename box.</summary>
public partial class AreaRowViewModel : ObservableObject
{
    public AreaRowViewModel(ProjectArea area)
    {
        Id = area.Id;
        Name = area.Name;
        editName = area.Name;
    }

    public Guid Id { get; }

    public string Name { get; }

    [ObservableProperty]
    private string editName;
}

/// <summary>Create, view, edit, and delete projects (Phase 1 acceptance), with milestones and areas.</summary>
public partial class ProjectsViewModel : ViewModelBase, IRefreshable
{
    private readonly IProjectService _projectService;
    private readonly IMilestoneService _milestoneService;
    private readonly IAreaService _areaService;
    private readonly List<Project> _allProjects = [];
    private bool _suppressSelectionLoad;

    // A save that's blocked on the active-project limit, awaiting a pause/kill decision.
    private ProjectDraft? _pendingDraft;
    private Guid? _pendingUpdateId;

    public ObservableCollection<Project> Projects { get; } = [];

    /// <summary>Active projects offered for pause/kill when the active limit is hit.</summary>
    public ObservableCollection<Project> ActiveProjectsToResolve { get; } = [];

    public ObservableCollection<Milestone> Milestones { get; } = [];

    public ObservableCollection<AreaRowViewModel> Areas { get; } = [];

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
    private string newAreaName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddMilestoneCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddAreaCommand))]
    [NotifyPropertyChangedFor(nameof(CanManageMilestones))]
    [NotifyPropertyChangedFor(nameof(ShowEditorIdentity))]
    private Project? selectedProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle))]
    [NotifyPropertyChangedFor(nameof(ShowEditorIdentity))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    [NotifyPropertyChangedFor(nameof(CanManageMilestones))]
    [NotifyCanExecuteChangedFor(nameof(AddMilestoneCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddAreaCommand))]
    private bool isCreatingNew = true;

    /// <summary>A standing project (e.g. "Life") is exempt from the active-project limit.</summary>
    [ObservableProperty]
    private bool draftIsStanding;

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

    public ProjectsViewModel(IProjectService projectService, IMilestoneService milestoneService, IAreaService areaService)
    {
        _projectService = projectService;
        _milestoneService = milestoneService;
        _areaService = areaService;
        BeginNewProject();
    }

    public string EditorTitle => IsCreatingNew ? "New Project" : "Edit Project";

    /// <summary>The editor header names the project being edited, in its identity colour.</summary>
    public bool ShowEditorIdentity => !IsCreatingNew && SelectedProject is not null;

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
            foreach (var project in _allProjects.Where(p => p.Status == ProjectStatus.Active && !p.IsStanding))
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
                TargetDate = project.TargetDate,
                IsStanding = project.IsStanding
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

    [RelayCommand(CanExecute = nameof(CanManageMilestones))]
    private async Task AddAreaAsync()
    {
        if (SelectedProject is null || string.IsNullOrWhiteSpace(NewAreaName))
        {
            return;
        }

        try
        {
            await _areaService.CreateAsync(SelectedProject.Id, NewAreaName);
            NewAreaName = string.Empty;
            await LoadAreasAsync(SelectedProject.Id);
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RenameAreaAsync(AreaRowViewModel? area)
    {
        if (area is null || SelectedProject is null || area.EditName.Trim() == area.Name)
        {
            return;
        }

        try
        {
            await _areaService.RenameAsync(area.Id, area.EditName);
            await LoadAreasAsync(SelectedProject.Id);
            FeedbackMessage = SelectedProject.IsStanding
                ? "Area renamed. Rename the matching Reminders list on your iPhone too, or the next sync re-creates the old name."
                : "Area renamed.";
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAreaAsync(AreaRowViewModel? area)
    {
        if (area is null || SelectedProject is null)
        {
            return;
        }

        try
        {
            await _areaService.DeleteAsync(area.Id);
            await LoadAreasAsync(SelectedProject.Id);
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    private async Task LoadAreasAsync(Guid projectId)
    {
        var areas = await _areaService.GetByProjectAsync(projectId);
        Areas.Clear();
        foreach (var area in areas)
        {
            Areas.Add(new AreaRowViewModel(area));
        }
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
        NewAreaName = string.Empty;
        DraftIsStanding = false;
        FeedbackMessage = string.Empty;
        Milestones.Clear();
        Areas.Clear();
    }

    private void LoadFromProject(Project project)
    {
        IsCreatingNew = false;
        DraftTitle = project.Title;
        DraftDescription = project.Description ?? string.Empty;
        NewMilestoneTitle = string.Empty;
        NewAreaName = string.Empty;
        DraftIsStanding = project.IsStanding;
        _ = LoadMilestonesAsync(project.Id);
        _ = LoadAreasAsync(project.Id);
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
            TargetDate = targetDate,
            IsStanding = DraftIsStanding
        };

        return true;
    }
}
