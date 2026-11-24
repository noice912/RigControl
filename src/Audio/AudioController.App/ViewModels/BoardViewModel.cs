using System.Collections.ObjectModel;
using System.Windows.Input;
using AudioController.Core.Models;

namespace AudioController.App.ViewModels;

/// <summary>One soundboard tab; wraps the persisted <see cref="Board"/> model.</summary>
public sealed class BoardViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _isEditing;
    private string _editName = "";

    public BoardViewModel(MainViewModel owner, Board model)
    {
        _owner = owner;
        Model = model;
        Slots = new ObservableCollection<SlotViewModel>();
        foreach (var slot in model.Slots)
            Slots.Add(new SlotViewModel(owner, this, slot));

        BeginRenameCommand = new RelayCommand(BeginRename);
        DeleteCommand = new RelayCommand(() => _owner.DeleteBoard(this));
    }

    public Board Model { get; }

    public ObservableCollection<SlotViewModel> Slots { get; }

    public ICommand BeginRenameCommand { get; }

    public ICommand DeleteCommand { get; }

    public string Name
    {
        get => Model.Name;
        private set
        {
            if (Model.Name == value) return;
            Model.Name = value;
            OnPropertyChanged();
            _owner.MarkDirty();
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set => SetProperty(ref _isEditing, value);
    }

    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value ?? "");
    }

    public void BeginRename()
    {
        EditName = Name;
        IsEditing = true;
    }

    public void CommitRename()
    {
        if (!IsEditing) return;
        var name = EditName.Trim();
        if (name.Length > 0) Name = name;
        IsEditing = false;
    }

    public void CancelRename() => IsEditing = false;

    internal SlotViewModel AddSlot(SoundSlot slot)
    {
        Model.Slots.Add(slot);
        var vm = new SlotViewModel(_owner, this, slot);
        Slots.Add(vm);
        return vm;
    }

    internal void RemoveSlot(SlotViewModel slot)
    {
        Model.Slots.Remove(slot.Model);
        Slots.Remove(slot);
    }
}
